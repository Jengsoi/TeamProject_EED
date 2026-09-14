using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.Networking;

// 07_통신프로토콜.md: TCP 연결 하나 = 세션 하나. 요청-응답은 correlationId로 짝짓고,
// 서버가 능동적으로 보내는 상태/결과/저장응답/오류 알림은 이벤트로 구독자에 전달한다.
public sealed class ServerConnection(string host, int port) : IDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Envelope>> _pending = new();
    private readonly ConcurrentDictionary<Guid, byte[]> _pendingImages = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readLoopCts;
    private int _connectionGeneration;
    private bool _disposed;

    public bool IsConnected { get; private set; }

    public event Action<InspectionStateChangedPayload>? StateChanged;
    public event Action<InspectionResultPayload, byte[]?>? ResultReceived;
    public event Action<SaveResultAckPayload>? SaveAckReceived;
    public event Action<ErrorNotificationPayload>? ErrorReceived;
    public event Action? Disconnected;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        TcpClient? newClient = null;
        try
        {
            if (_disposed) return false;

            var previousCts = Interlocked.Exchange(ref _readLoopCts, null);
            var previousClient = Interlocked.Exchange(ref _client, null);
            _stream = null;
            IsConnected = false;
            Interlocked.Increment(ref _connectionGeneration);
            previousCts?.Cancel();
            previousCts?.Dispose();
            try { previousClient?.Close(); } catch (Exception) { /* best effort */ }
            FailPendingRequests();

            newClient = new TcpClient();
            await newClient.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var stream = newClient.GetStream();
            var readLoopCts = new CancellationTokenSource();
            int generation = Volatile.Read(ref _connectionGeneration);

            _client = newClient;
            _stream = stream;
            _readLoopCts = readLoopCts;
            IsConnected = true;
            newClient = null;
            _ = Task.Run(() => ReadLoopAsync(stream, generation, readLoopCts.Token), CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            IsConnected = false;
            return false;
        }
        finally
        {
            try { newClient?.Close(); } catch (Exception) { /* best effort */ }
            _connectLock.Release();
        }
    }

    public async Task<bool> ConnectWithRetryAsync(int attempts, TimeSpan delay, CancellationToken ct = default)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (await ConnectAsync(ct).ConfigureAwait(false)) return true;
            if (i < attempts - 1) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<Envelope> RequestAsync<TPayload>(string type, TPayload payload, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var stream = _stream;
        if (!IsConnected || stream is null) throw new IOException("서버에 연결되어 있지 않습니다.");

        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = tcs;

        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await ProtocolMessage.SendAsync(stream, type, correlationId, payload, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            _pending.TryRemove(correlationId, out _);
            throw;
        }

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        await using var registration = linked.Token.Register(() => tcs.TrySetCanceled());
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    public bool TryTakeImage(Guid correlationId, out byte[]? image) => _pendingImages.TryRemove(correlationId, out image);

    public async Task SendAsync<TPayload>(string type, TPayload payload, CancellationToken ct = default)
    {
        var stream = _stream;
        if (!IsConnected || stream is null) return;
        var correlationId = Guid.NewGuid();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ProtocolMessage.SendAsync(stream, type, correlationId, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SendFrameAsync(FrameMetaPayload meta, byte[] jpegBytes, CancellationToken ct = default)
    {
        var stream = _stream;
        if (!IsConnected || stream is null) return;
        var correlationId = Guid.NewGuid();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ProtocolMessage.SendAsync(stream, MessageTypes.FrameMeta, correlationId, meta, ct).ConfigureAwait(false);
            await ProtocolMessage.SendImageAsync(stream, jpegBytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, int generation, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var envelope = await ProtocolMessage.ReceiveEnvelopeAsync(stream, ct).ConfigureAwait(false);

                // 07_통신프로토콜.md §4: imageAvailable=true인 InspectionResult/InspectionDetailResponse는
                // 바로 뒤에 0x02 이미지 프레임이 온다. 다음 envelope를 읽기 전에 반드시 먼저 소비해야 한다.
                if (envelope.Type == MessageTypes.InspectionResult)
                {
                    var payload = envelope.DeserializePayload<InspectionResultPayload>();
                    byte[]? image = payload.ImageAvailable
                        ? await ProtocolMessage.ReceiveImageAsync(stream, ct).ConfigureAwait(false)
                        : null;
                    if (_pending.TryRemove(envelope.CorrelationId, out var tcsResult)) tcsResult.TrySetResult(envelope);
                    ResultReceived?.Invoke(payload, image);
                    continue;
                }

                if (envelope.Type == MessageTypes.InspectionDetailResponse)
                {
                    var payload = envelope.DeserializePayload<InspectionDetailResponsePayload>();
                    if (payload.ImageAvailable)
                    {
                        var image = await ProtocolMessage.ReceiveImageAsync(stream, ct).ConfigureAwait(false);
                        _pendingImages[envelope.CorrelationId] = image;
                    }
                    if (_pending.TryRemove(envelope.CorrelationId, out var tcsDetail)) tcsDetail.TrySetResult(envelope);
                    continue;
                }

                if (_pending.TryRemove(envelope.CorrelationId, out var tcs))
                {
                    tcs.TrySetResult(envelope);
                    continue;
                }

                switch (envelope.Type)
                {
                    case MessageTypes.InspectionStateChanged:
                        StateChanged?.Invoke(envelope.DeserializePayload<InspectionStateChangedPayload>());
                        break;
                    case MessageTypes.SaveResultAck:
                        SaveAckReceived?.Invoke(envelope.DeserializePayload<SaveResultAckPayload>());
                        break;
                    case MessageTypes.ErrorNotification:
                        ErrorReceived?.Invoke(envelope.DeserializePayload<ErrorNotificationPayload>());
                        break;
                }
            }
        }
        catch (Exception)
        {
            // 연결 종료/오류 — 아래 finally에서 정리한다.
        }
        finally
        {
            HandleConnectionLost(generation);
        }
    }

    private void HandleConnectionLost(int generation)
    {
        if (generation != Volatile.Read(ref _connectionGeneration)) return;

        IsConnected = false;
        _stream = null;
        var lostClient = Interlocked.Exchange(ref _client, null);
        try { lostClient?.Close(); } catch (Exception) { /* best effort */ }
        var lostCts = Interlocked.Exchange(ref _readLoopCts, null);
        lostCts?.Dispose();
        FailPendingRequests();
        Disconnected?.Invoke();
    }

    private void FailPendingRequests()
    {
        foreach (var kvp in _pending)
            kvp.Value.TrySetException(new IOException("서버 연결이 끊어졌습니다."));
        _pending.Clear();
        _pendingImages.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _connectionGeneration);
        var readLoopCts = Interlocked.Exchange(ref _readLoopCts, null);
        readLoopCts?.Cancel();
        readLoopCts?.Dispose();
        var client = Interlocked.Exchange(ref _client, null);
        try { client?.Close(); } catch (Exception) { /* 정리 중 예외 무시 */ }
        _stream = null;
        IsConnected = false;
        FailPendingRequests();
        _connectLock.Dispose();
        _writeLock.Dispose();
    }
}
