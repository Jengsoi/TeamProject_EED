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
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Envelope>> _pending = new();
    private readonly ConcurrentDictionary<Guid, byte[]> _pendingImages = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private long _connectionGeneration;
    private bool _disposed;

    public bool IsConnected { get; private set; }

    public event Action<InspectionStateChangedPayload>? StateChanged;
    public event Action<InspectionResultPayload, byte[]?>? ResultReceived;
    public event Action<SaveResultAckPayload>? SaveAckReceived;
    public event Action<ErrorNotificationPayload>? ErrorReceived;
    public event Action? Disconnected;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return false;
            IsConnected = false;
            _readLoopCts?.Cancel();
            _client?.Close();

            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var stream = client.GetStream();
            _client = client;
            _stream = stream;
            IsConnected = true;

            var readLoopCts = new CancellationTokenSource();
            _readLoopCts = readLoopCts;
            long generation = Interlocked.Increment(ref _connectionGeneration);
            _readLoopTask = Task.Run(() => ReadLoopAsync(stream, generation, readLoopCts.Token), CancellationToken.None);
            return true;
        }
        catch (Exception)
        {
            IsConnected = false;
            return false;
        }
        finally
        {
            _connectionLock.Release();
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
        if (!IsConnected || _stream is null) throw new IOException("서버에 연결되어 있지 않습니다.");

        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = tcs;

        try
        {
            await WriteLockedAsync(
                stream => ProtocolMessage.SendAsync(stream, type, correlationId, payload, ct), ct).ConfigureAwait(false);
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
        if (!IsConnected || _stream is null) return;
        var correlationId = Guid.NewGuid();
        await WriteLockedAsync(
            stream => ProtocolMessage.SendAsync(stream, type, correlationId, payload, ct), ct).ConfigureAwait(false);
    }

    public async Task SendFrameAsync(FrameMetaPayload meta, byte[] jpegBytes, CancellationToken ct = default)
    {
        if (!IsConnected || _stream is null) return;
        var correlationId = Guid.NewGuid();
        await WriteLockedAsync(async stream =>
        {
            await ProtocolMessage.SendAsync(stream, MessageTypes.FrameMeta, correlationId, meta, ct).ConfigureAwait(false);
            await ProtocolMessage.SendImageAsync(stream, jpegBytes, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task WriteLockedAsync(Func<NetworkStream, Task> write, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("서버에 연결되어 있지 않습니다.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await write(stream).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(NetworkStream stream, long generation, CancellationToken ct)
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
            if (Interlocked.Read(ref _connectionGeneration) == generation)
            {
                IsConnected = false;
                foreach (var kvp in _pending) kvp.Value.TrySetException(new IOException("서버 연결이 끊어졌습니다."));
                _pending.Clear();
                Disconnected?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _connectionGeneration);
        _readLoopCts?.Cancel();
        try { _client?.Close(); } catch (Exception) { /* 정리 중 예외 무시 */ }
        IsConnected = false;
        foreach (var kvp in _pending)
            kvp.Value.TrySetException(new ObjectDisposedException(nameof(ServerConnection)));
        _pending.Clear();
    }
}
