using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SafetyVision.Core.Analysis;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using SafetyVision.Core.StateMachine;
using SafetyVision.Data.Services;
using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;
using SafetyVision.Protocol.Framing;
using SafetyVision.Server.Inference;

namespace SafetyVision.Server.Networking;

// TCP 연결(세션) 하나를 담당한다. 07_통신프로토콜.md의 메시지 카탈로그를 처리하고,
// 판정은 Core의 InspectionStateMachine에 위임한다.
public sealed class ClientSession(
    TcpClient client,
    SafetyVisionOptions options,
    IPpeDetector detector,
    IServiceScopeFactory scopeFactory,
    ILogger<ClientSession> logger)
{
    private readonly NetworkStream _stream = client.GetStream();
    private readonly ClientQueryHandler _queries = new(scopeFactory);
    private readonly InspectionSessionContext _inspection = new(options);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // _inspection.StateMachine/_inspection.LastDetection/_inspection.LastFrameJpeg/_inspection.AnalysisFrames/_inspection.PendingSave는 원래 단일 수신 루프에서만
    // 건드렸지만, 프레임 처리를 별도 백그라운드 루프로 분리하면서(아래 참고) 다른 요청 처리(재시도 등)와
    // 동시에 접근될 수 있다. 이 락으로 두 쪽 모두를 보호한다.
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // 프레임 처리(추론)가 전송 속도를 못 따라가도 지연이 계속 누적되지 않도록, 항상 "가장 최근 프레임 1개"만
    // 유지한다. 처리 중에 새 프레임이 도착하면 처리 대기 중이던 이전 프레임은 버려진다(DropOldest).
    private readonly Channel<(Envelope Envelope, byte[] ImageBytes)> _frameChannel =
        Channel.CreateBounded<(Envelope, byte[])>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private bool _isAuthenticated;
    public async Task RunAsync(CancellationToken serverShutdownToken)
    {
        var frameProcessingTask = ProcessFramesAsync(serverShutdownToken);
        try
        {
            while (!serverShutdownToken.IsCancellationRequested)
            {
                var envelope = await ProtocolMessage.ReceiveEnvelopeAsync(_stream, serverShutdownToken).ConfigureAwait(false);
                try
                {
                    await DispatchAsync(envelope, serverShutdownToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not (IOException or EndOfStreamException or OperationCanceledException))
                {
                    logger.LogError(ex, "메시지 처리 중 오류: {Type}", envelope.Type);
                    await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InvalidRequest, "요청을 처리하지 못했습니다.", serverShutdownToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            logger.LogInformation("클라이언트 연결 종료: {Reason}", ex.GetType().Name);
        }
        finally
        {
            _frameChannel.Writer.TryComplete();
            try { await frameProcessingTask.ConfigureAwait(false); }
            catch (Exception) { /* 프레임 처리 루프 종료 중 예외는 연결 종료 처리에 영향 주지 않는다 */ }

            await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _inspection.StateMachine.Cancel(CancelReason.ConnectionClosed, NowSeconds()); }
            finally { _stateLock.Release(); }

            try { client.Close(); } catch (Exception) { /* 연결 정리 과정의 예외는 무시한다 */ }
        }
    }

    private async Task DispatchAsync(Envelope envelope, CancellationToken ct)
    {
        if (envelope.Type != MessageTypes.LoginRequest && !_isAuthenticated)
        {
            // FrameMeta 바로 뒤에는 이미지 프레임이 붙는다. 인증 오류라도 이를 소비해야
            // 다음 메시지의 프레이밍 경계가 깨지지 않는다.
            if (envelope.Type == MessageTypes.FrameMeta)
                await FrameCodec.ReadAsync(_stream, ct).ConfigureAwait(false);
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.Unauthorized,
                "로그인이 필요한 요청입니다.", ct).ConfigureAwait(false);
            return;
        }

        await (envelope.Type switch
        {
            MessageTypes.LoginRequest => HandleLoginAsync(envelope, ct),
            MessageTypes.LogoutRequest => HandleLogoutAsync(envelope, ct),
            MessageTypes.DashboardStatsRequest => HandleDashboardStatsAsync(envelope, ct),
            MessageTypes.StatisticsRequest => HandleStatisticsAsync(envelope, ct),
            MessageTypes.InspectionSessionStart => HandleSessionStartAsync(envelope, ct),
            MessageTypes.FrameMeta => HandleFrameMetaAsync(envelope, ct),
            MessageTypes.RetryInspectionRequest => HandleRetryInspectionAsync(envelope, ct),
            MessageTypes.RetrySaveRequest => HandleRetrySaveAsync(envelope, ct),
            MessageTypes.InspectionSessionEnd => HandleSessionEndAsync(envelope, ct),
            MessageTypes.HistoryPageRequest => HandleHistoryPageAsync(envelope, ct),
            MessageTypes.InspectionDetailRequest => HandleInspectionDetailAsync(envelope, ct),
            _ => TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InvalidRequest, "알 수 없는 메시지 유형입니다.", ct)
        }).ConfigureAwait(false);
    }

    private async Task HandleLoginAsync(Envelope envelope, CancellationToken ct)
    {
        var req = envelope.DeserializePayload<LoginRequestPayload>();
        using var scope = scopeFactory.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var (success, displayName) = await auth.ValidateLoginAsync(req.LoginId, req.Password, ct).ConfigureAwait(false);
        _isAuthenticated = success;

        var payload = success
            ? new LoginResponsePayload(true, displayName, null)
            : new LoginResponsePayload(false, null, "아이디 또는 비밀번호를 확인해 주세요.");
        await SendAsync(MessageTypes.LoginResponse, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleLogoutAsync(Envelope envelope, CancellationToken ct)
    {
        _isAuthenticated = false;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try { _inspection.StateMachine.Cancel(CancelReason.LoggedOut, NowSeconds()); }
        finally { _stateLock.Release(); }
        await SendAsync(MessageTypes.LogoutResponse, envelope.CorrelationId, new LogoutResponsePayload(true), ct).ConfigureAwait(false);
    }

    private async Task HandleDashboardStatsAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = await _queries.GetDashboardAsync(ct).ConfigureAwait(false);
        await SendAsync(MessageTypes.DashboardStatsResponse, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleStatisticsAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = await _queries.GetStatisticsAsync(ct).ConfigureAwait(false);
        await SendAsync(MessageTypes.StatisticsResponse, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleSessionStartAsync(Envelope envelope, CancellationToken ct)
    {
        var req = envelope.DeserializePayload<InspectionSessionStartPayload>();
        if (!string.IsNullOrWhiteSpace(req.CameraName))
            _inspection.CameraName = req.CameraName;

        var payload = new InspectionSessionStartedPayload(true, null, options.RoiLeft, options.RoiTop, options.RoiRight, options.RoiBottom);
        await SendAsync(MessageTypes.InspectionSessionStarted, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleSessionEndAsync(Envelope envelope, CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _inspection.StateMachine.Cancel(CancelReason.ScreenLeft, NowSeconds());
            _inspection.AnalysisFrames.Clear();
            _inspection.LastFrameJpeg = null;
            _inspection.LastDetection = null;
        }
        finally { _stateLock.Release(); }
    }

    // 네트워크에서 프레임을 빠르게 받아 채널에 넣기만 한다(추론 없음). 실제 판정은 ProcessFramesAsync가
    // 별도로 처리하므로, 이 메서드가 오래 걸리지 않아 다음 메시지 수신이 밀리지 않는다.
    private async Task HandleFrameMetaAsync(Envelope envelope, CancellationToken ct)
    {
        var (imageType, imageBytes) = await FrameCodec.ReadAsync(_stream, ct).ConfigureAwait(false);
        if (imageType != ProtocolMessageType.Image)
        {
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InvalidRequest, "이미지 프레임이 필요합니다.", ct).ConfigureAwait(false);
            return;
        }

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try { _inspection.LastFrameJpeg = imageBytes; }
        finally { _stateLock.Release(); }

        if (!detector.IsAvailable)
        {
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.ModelUnavailable,
                detector.UnavailableReason ?? "모델을 불러올 수 없습니다. 모델 파일을 확인해 주세요.", ct).ConfigureAwait(false);
            return;
        }

        // 용량 1 + DropOldest라 항상 즉시 반환되며, 처리 대기 중이던 이전 프레임이 있었다면 버려진다.
        await _frameChannel.Writer.WriteAsync((envelope, imageBytes), ct).ConfigureAwait(false);
    }

    // 채널에서 "가장 최근 프레임"을 하나씩 꺼내 실제 추론·상태 갱신을 수행하는 백그라운드 루프.
    // 추론이 전송 속도보다 느려도, 밀린 프레임을 순서대로 다 처리하는 게 아니라 최신 것만 처리하므로
    // 지연이 계속 누적되지 않는다.
    private async Task ProcessFramesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (envelope, imageBytes) in _frameChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ProcessFrameAsync(envelope, imageBytes, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not (IOException or EndOfStreamException or OperationCanceledException))
                {
                    logger.LogError(ex, "프레임 처리 중 오류");
                    await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InvalidRequest, "요청을 처리하지 못했습니다.", ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 서버/연결 종료로 인한 정상적인 취소.
        }
    }

    private async Task ProcessFrameAsync(Envelope envelope, byte[] imageBytes, CancellationToken ct)
    {
        DetectionFrame detection;
        try
        {
            detection = detector.Detect(imageBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "추론 실패");
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _inspection.StateMachine.Cancel(CancelReason.InferenceError, NowSeconds());
                _inspection.AnalysisFrames.Clear();
            }
            finally { _stateLock.Release(); }
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InferenceError, "AI 분석 중 오류가 발생했습니다. 화면에 다시 진입해 주세요.", ct).ConfigureAwait(false);
            await SendStateAsync(ct).ConfigureAwait(false);
            return;
        }

        var eval = FrameAnalyzer.Evaluate(detection.Boxes, detection.Width, detection.Height, options);

        logger.LogInformation(
        "검출={Boxes}",
        string.Join(", ",
            detection.Boxes.Select(x =>
                $"{x.Class}({x.Confidence:F2})")));

        bool justCompleted;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _inspection.LastDetection = detection;
            bool wasInspecting = _inspection.StateMachine.State == InspectionState.Inspecting;
            int beforeCount = _inspection.StateMachine.FrameVotes.Count;
            int beforeGeneration = _inspection.StateMachine.Generation;
            justCompleted = _inspection.StateMachine.ProcessFrame(eval, NowSeconds());

            if (_inspection.StateMachine.Generation != beforeGeneration)
                _inspection.AnalysisFrames.Clear();

            if (wasInspecting && eval.Condition == PersonRoiCondition.Qualified && _inspection.StateMachine.FrameVotes.Count > beforeCount)
            {
                var target = FrameAnalyzer.FindSingleRoiPerson(detection.Boxes, detection.Width, detection.Height, options);
                _inspection.AnalysisFrames.Add(new AnalysisFrameRecord(imageBytes, detection.Boxes, target?.Confidence ?? 0));
            }
        }
        finally { _stateLock.Release(); }

        if (justCompleted)
            await OnInspectionCompletedAsync(ct).ConfigureAwait(false);
        else
            await SendStateAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleRetryInspectionAsync(Envelope envelope, CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inspection.LastDetection is not null)
            {
                var eval = FrameAnalyzer.Evaluate(_inspection.LastDetection.Boxes, _inspection.LastDetection.Width, _inspection.LastDetection.Height, options);
                if (_inspection.StateMachine.TryRetry(eval, NowSeconds()))
                    _inspection.AnalysisFrames.Clear();
            }
        }
        finally { _stateLock.Release(); }
        await SendStateAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleRetrySaveAsync(Envelope envelope, CancellationToken ct)
    {
        var req = envelope.DeserializePayload<RetrySaveRequestPayload>();

        bool canRetry;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            canRetry = Guid.TryParse(req.InspectionKey, out var key)
                && key == _inspection.StateMachine.InspectionKey
                && _inspection.PendingSave is not null
                && _inspection.StateMachine.TryRetrySave();
        }
        finally { _stateLock.Release(); }

        if (!canRetry)
        {
            await SendAsync(MessageTypes.SaveResultAck, envelope.CorrelationId,
                new SaveResultAckPayload(SaveStatusCodes.SaveFailed, "재시도할 저장이 없습니다."), ct).ConfigureAwait(false);
            return;
        }

        await PersistPendingSaveAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleHistoryPageAsync(Envelope envelope, CancellationToken ct)
    {
        var req = envelope.DeserializePayload<HistoryPageRequestPayload>();
        var payload = await _queries.GetHistoryAsync(req, ct).ConfigureAwait(false);
        await SendAsync(MessageTypes.HistoryPageResponse, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleInspectionDetailAsync(Envelope envelope, CancellationToken ct)
    {
        var req = envelope.DeserializePayload<InspectionDetailRequestPayload>();
        var detail = await _queries.GetInspectionDetailAsync(req.Id, ct).ConfigureAwait(false);
        if (detail is null)
        {
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InvalidRequest, "검사 기록을 찾을 수 없습니다.", ct).ConfigureAwait(false);
            return;
        }

        await SendWithOptionalImageAsync(
            MessageTypes.InspectionDetailResponse, envelope.CorrelationId,
            detail.Payload, detail.Image, ct).ConfigureAwait(false);
    }

    private async Task OnInspectionCompletedAsync(CancellationToken ct)
    {
        byte[]? representativeJpeg;
        double? personConfidence;
        InspectionOutcome outcome;
        Guid inspectionKey;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            outcome = _inspection.StateMachine.Outcome!;
            inspectionKey = _inspection.StateMachine.InspectionKey;
            personConfidence = null;
            representativeJpeg = null;

            if (outcome.RepresentativeFrameIndex >= 0 && outcome.RepresentativeFrameIndex < _inspection.AnalysisFrames.Count)
            {
                var frame = _inspection.AnalysisFrames[outcome.RepresentativeFrameIndex];
                representativeJpeg = ImageAnnotator.DrawBoxes(frame.Jpeg, frame.Boxes, options.JpegQuality);
                personConfidence = frame.PersonConfidence;
            }
            else if (_inspection.LastFrameJpeg is not null)
            {
                representativeJpeg = _inspection.LastFrameJpeg; // N=0: 마지막 프레임을 박스 없이 사용
            }

            _inspection.PendingSave = new SaveInspectionRequest(
                inspectionKey, DateTime.UtcNow, outcome.Result, outcome.Items,
                representativeJpeg, personConfidence, _inspection.CameraName, options.ModelName, options.ModelVersion);
            _inspection.AnalysisFrames.Clear();
        }
        finally { _stateLock.Release(); }

        // 저장은 클라이언트 연결 상태와 무관하게 먼저 끝낸다. 결과 전송이 실패해도 이력은 남는다.
        await PersistPendingSaveAsync(ct, notifyClient: false).ConfigureAwait(false);

        string saveState;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try { saveState = SaveStateCode(_inspection.StateMachine.SaveState); }
        finally { _stateLock.Release(); }

        await SendResultMessageAsync(inspectionKey, outcome, representativeJpeg, saveState, ct).ConfigureAwait(false);
    }

    private async Task PersistPendingSaveAsync(CancellationToken ct, bool notifyClient = true)
    {
        SaveInspectionRequest? pending;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try { pending = _inspection.PendingSave; }
        finally { _stateLock.Release(); }

        if (pending is null) return;

        using var scope = scopeFactory.CreateScope();
        var saveService = scope.ServiceProvider.GetRequiredService<InspectionSaveService>();
        var result = await saveService.SaveAsync(pending, ct).ConfigureAwait(false);

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (result.Outcome == SaveOutcome.Success) _inspection.StateMachine.MarkSaveSucceeded();
            else _inspection.StateMachine.MarkSaveFailed();
        }
        finally { _stateLock.Release(); }

        if (!notifyClient)
            return;

        if (result.Outcome == SaveOutcome.Success)
        {
            await SendAsync(MessageTypes.SaveResultAck, Guid.NewGuid(), new SaveResultAckPayload(SaveStatusCodes.Saved, null), ct).ConfigureAwait(false);
        }
        else
        {
            await SendAsync(MessageTypes.SaveResultAck, Guid.NewGuid(),
                new SaveResultAckPayload(SaveStatusCodes.SaveFailed, "결과를 저장하지 못했습니다."), ct).ConfigureAwait(false);
        }
    }

    private Task SendResultMessageAsync(Guid inspectionKey, InspectionOutcome outcome, byte[]? representativeJpeg, string saveState, CancellationToken ct)
    {
        var items = outcome.Items
            .Select(i => new EquipmentResultPayload(EquipmentClassMap.ToDbCode(i.Code), i.Status.ToDbCode(), i.Score))
            .ToList();
        var payload = new InspectionResultPayload(
            inspectionKey.ToString(), outcome.Result.ToDbCode(), items,
            representativeJpeg is not null, saveState);

        return SendWithOptionalImageAsync(MessageTypes.InspectionResult, Guid.NewGuid(), payload, representativeJpeg, ct);
    }

    private async Task SendWithOptionalImageAsync<T>(string type, Guid correlationId, T payload, byte[]? image, CancellationToken ct)
    {
        await WriteLockedAsync(async () =>
        {
            await ProtocolMessage.SendAsync(_stream, type, correlationId, payload, ct).ConfigureAwait(false);
            if (image is not null)
                await ProtocolMessage.SendImageAsync(_stream, image, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task SendStateAsync(CancellationToken ct)
    {
        InspectionState state;
        string guidance;
        SaveState saveState;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            state = _inspection.StateMachine.State;
            guidance = _inspection.StateMachine.GuidanceMessage;
            saveState = _inspection.StateMachine.SaveState;
        }
        finally { _stateLock.Release(); }

        await SendAsync(
            MessageTypes.InspectionStateChanged, Guid.NewGuid(),
            new InspectionStateChangedPayload(StateCode(state), guidance, SaveStateCode(saveState)),
            ct).ConfigureAwait(false);
    }

    private async Task SendAsync<T>(string type, Guid correlationId, T payload, CancellationToken ct)
    {
        await WriteLockedAsync(
            () => ProtocolMessage.SendAsync(_stream, type, correlationId, payload, ct), ct).ConfigureAwait(false);
    }

    private async Task WriteLockedAsync(Func<Task> write, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await write().ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private async Task TrySendErrorAsync(Guid correlationId, string code, string message, CancellationToken ct)
    {
        try
        {
            await SendAsync(MessageTypes.ErrorNotification, correlationId, new ErrorNotificationPayload(code, message), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 연결이 이미 끊겼으면 오류 통지도 보낼 수 없다.
        }
    }

    private static string StateCode(InspectionState s) => s switch
    {
        InspectionState.Waiting => "WAITING",
        InspectionState.PersonDetected => "PERSON_DETECTED",
        InspectionState.Inspecting => "INSPECTING",
        InspectionState.Result => "RESULT",
        _ => "WAITING"
    };

    private static string SaveStateCode(SaveState s) => s switch
    {
        SaveState.None => "NONE",
        SaveState.Saving => SaveStatusCodes.Saving,
        SaveState.Saved => SaveStatusCodes.Saved,
        SaveState.Failed => SaveStatusCodes.SaveFailed,
        _ => "NONE"
    };

    private double NowSeconds() => _clock.Elapsed.TotalSeconds;
}
