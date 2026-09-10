using System.Diagnostics;
using System.Net.Sockets;
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
    private readonly InspectionStateMachine _stateMachine = new(options);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<AnalysisFrameRecord> _analysisFrames = [];

    private DetectionFrame? _lastDetection;
    private byte[]? _lastFrameJpeg;
    private SaveInspectionRequest? _pendingSave;

    private sealed record AnalysisFrameRecord(byte[] Jpeg, IReadOnlyList<DetectedBox> Boxes, double PersonConfidence);

    public async Task RunAsync(CancellationToken serverShutdownToken)
    {
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
            _stateMachine.Cancel(CancelReason.ConnectionClosed, NowSeconds());
            try { client.Close(); } catch (Exception) { /* 연결 정리 과정의 예외는 무시한다 */ }
        }
    }

    private Task DispatchAsync(Envelope envelope, CancellationToken ct) => envelope.Type switch
    {
        MessageTypes.LoginRequest => HandleLoginAsync(envelope, ct),
        MessageTypes.LogoutRequest => HandleLogoutAsync(envelope, ct),
        MessageTypes.DashboardStatsRequest => HandleDashboardStatsAsync(envelope, ct),
        MessageTypes.InspectionSessionStart => HandleSessionStartAsync(envelope, ct),
        MessageTypes.FrameMeta => HandleFrameMetaAsync(envelope, ct),
        MessageTypes.RetryInspectionRequest => HandleRetryInspectionAsync(envelope, ct),
        MessageTypes.RetrySaveRequest => HandleRetrySaveAsync(envelope, ct),
        MessageTypes.InspectionSessionEnd => HandleSessionEndAsync(envelope, ct),
        MessageTypes.HistoryPageRequest => HandleHistoryPageAsync(envelope, ct),
        MessageTypes.InspectionDetailRequest => HandleInspectionDetailAsync(envelope, ct),
        _ => TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InvalidRequest, "알 수 없는 메시지 유형입니다.", ct)
    };

    private async Task HandleLoginAsync(Envelope envelope, CancellationToken ct)
    {
        var req = envelope.DeserializePayload<LoginRequestPayload>();
        using var scope = scopeFactory.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var (success, displayName) = await auth.ValidateLoginAsync(req.LoginId, req.Password, ct).ConfigureAwait(false);

        var payload = success
            ? new LoginResponsePayload(true, displayName, null)
            : new LoginResponsePayload(false, null, "아이디 또는 비밀번호를 확인해 주세요.");
        await SendAsync(MessageTypes.LoginResponse, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleLogoutAsync(Envelope envelope, CancellationToken ct)
    {
        _stateMachine.Cancel(CancelReason.LoggedOut, NowSeconds());
        await SendAsync(MessageTypes.LogoutResponse, envelope.CorrelationId, new LogoutResponsePayload(true), ct).ConfigureAwait(false);
    }

    private async Task HandleDashboardStatsAsync(Envelope envelope, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DashboardQueryService>();
        var stats = await svc.GetStatsAsync(ct).ConfigureAwait(false);

        var payload = new DashboardStatsResponsePayload(
            stats.Total, stats.Normal, stats.CheckRequired, stats.Unconfirmed,
            stats.EquipmentRates.Select(r => new EquipmentRatePayload(EquipmentClassMap.ToDbCode(r.Code), r.WornRatio)).ToList(),
            stats.Recent.Select(r => new RecentInspectionPayload(
                r.Id, new DateTimeOffset(DateTime.SpecifyKind(r.InspectedAtUtc, DateTimeKind.Utc)),
                "CAM 01", r.Hardhat, r.Vest, r.Mask, r.Result, r.HasImage)).ToList());

        await SendAsync(MessageTypes.DashboardStatsResponse, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleSessionStartAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = new InspectionSessionStartedPayload(true, null, options.RoiLeft, options.RoiTop, options.RoiRight, options.RoiBottom);
        await SendAsync(MessageTypes.InspectionSessionStarted, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private Task HandleSessionEndAsync(Envelope envelope, CancellationToken ct)
    {
        _stateMachine.Cancel(CancelReason.ScreenLeft, NowSeconds());
        _analysisFrames.Clear();
        _lastFrameJpeg = null;
        _lastDetection = null;
        return Task.CompletedTask;
    }

    private async Task HandleFrameMetaAsync(Envelope envelope, CancellationToken ct)
    {
        var (imageType, imageBytes) = await FrameCodec.ReadAsync(_stream, ct).ConfigureAwait(false);
        if (imageType != ProtocolMessageType.Image)
        {
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InvalidRequest, "이미지 프레임이 필요합니다.", ct).ConfigureAwait(false);
            return;
        }

        _lastFrameJpeg = imageBytes;

        if (!detector.IsAvailable)
        {
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.ModelUnavailable,
                detector.UnavailableReason ?? "모델을 불러올 수 없습니다. 모델 파일을 확인해 주세요.", ct).ConfigureAwait(false);
            return;
        }

        DetectionFrame detection;
        try
        {
            detection = detector.Detect(imageBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "추론 실패");
            _stateMachine.Cancel(CancelReason.InferenceError, NowSeconds());
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InferenceError, "AI 분석 중 오류가 발생했습니다. 화면에 다시 진입해 주세요.", ct).ConfigureAwait(false);
            await SendStateAsync(ct).ConfigureAwait(false);
            return;
        }

        _lastDetection = detection;
        var eval = FrameAnalyzer.Evaluate(detection.Boxes, detection.Width, detection.Height, options);

        bool wasInspecting = _stateMachine.State == InspectionState.Inspecting;
        int beforeCount = _stateMachine.FrameVotes.Count;
        bool justCompleted = _stateMachine.ProcessFrame(eval, NowSeconds());

        if (wasInspecting && eval.Condition == PersonRoiCondition.Qualified && _stateMachine.FrameVotes.Count > beforeCount)
        {
            var target = FrameAnalyzer.FindSingleRoiPerson(detection.Boxes, detection.Width, detection.Height, options);
            _analysisFrames.Add(new AnalysisFrameRecord(imageBytes, detection.Boxes, target?.Confidence ?? 0));
        }

        if (justCompleted)
            await OnInspectionCompletedAsync(ct).ConfigureAwait(false);
        else
            await SendStateAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleRetryInspectionAsync(Envelope envelope, CancellationToken ct)
    {
        if (_lastDetection is not null)
        {
            var eval = FrameAnalyzer.Evaluate(_lastDetection.Boxes, _lastDetection.Width, _lastDetection.Height, options);
            _stateMachine.TryRetry(eval, NowSeconds());
        }
        await SendStateAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleRetrySaveAsync(Envelope envelope, CancellationToken ct)
    {
        var req = envelope.DeserializePayload<RetrySaveRequestPayload>();
        if (!Guid.TryParse(req.InspectionKey, out var key)
            || key != _stateMachine.InspectionKey
            || _pendingSave is null
            || !_stateMachine.TryRetrySave())
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
        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<HistoryQueryService>();
        var page = await svc.GetPageAsync(req.Page, req.FromUtc?.UtcDateTime, req.ToUtc?.UtcDateTime, req.ResultFilter, ct).ConfigureAwait(false);

        var payload = new HistoryPageResponsePayload(page.Page, page.TotalPages, page.TotalCount,
            page.Rows.Select(r => new HistoryRowPayload(
                r.Id, new DateTimeOffset(DateTime.SpecifyKind(r.InspectedAtUtc, DateTimeKind.Utc)),
                r.Hardhat, r.Vest, r.Mask, r.Result)).ToList());

        await SendAsync(MessageTypes.HistoryPageResponse, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleInspectionDetailAsync(Envelope envelope, CancellationToken ct)
    {
        var req = envelope.DeserializePayload<InspectionDetailRequestPayload>();
        using var scope = scopeFactory.CreateScope();
        var history = scope.ServiceProvider.GetRequiredService<HistoryQueryService>();
        var saveService = scope.ServiceProvider.GetRequiredService<InspectionSaveService>();

        var inspection = await history.GetDetailAsync(req.Id, ct).ConfigureAwait(false);
        if (inspection is null)
        {
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InvalidRequest, "검사 기록을 찾을 수 없습니다.", ct).ConfigureAwait(false);
            return;
        }

        byte[]? imageBytes = null;
        string? missingMessage = "이미지 파일을 찾을 수 없습니다.";
        if (!string.IsNullOrEmpty(inspection.ImagePath))
        {
            var fullPath = saveService.ResolveImageFullPath(inspection.ImagePath);
            if (File.Exists(fullPath))
            {
                imageBytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
                missingMessage = null;
            }
        }

        var items = inspection.Items
            .Select(i => new EquipmentResultPayload(i.EquipmentCode, i.Status, i.Score))
            .ToList();
        var payload = new InspectionDetailResponsePayload(
            inspection.Id, new DateTimeOffset(DateTime.SpecifyKind(inspection.InspectedAt, DateTimeKind.Utc)),
            items, inspection.Result, imageBytes is not null, missingMessage);

        await SendAsync(MessageTypes.InspectionDetailResponse, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
        if (imageBytes is not null) await SendImageAsync(imageBytes, ct).ConfigureAwait(false);
    }

    private async Task OnInspectionCompletedAsync(CancellationToken ct)
    {
        var outcome = _stateMachine.Outcome!;
        byte[]? representativeJpeg = null;
        double? personConfidence = null;

        if (outcome.RepresentativeFrameIndex >= 0 && outcome.RepresentativeFrameIndex < _analysisFrames.Count)
        {
            var frame = _analysisFrames[outcome.RepresentativeFrameIndex];
            representativeJpeg = ImageAnnotator.DrawBoxes(frame.Jpeg, frame.Boxes, options.JpegQuality);
            personConfidence = frame.PersonConfidence;
        }
        else if (_lastFrameJpeg is not null)
        {
            representativeJpeg = _lastFrameJpeg; // N=0: 마지막 프레임을 박스 없이 사용
        }

        await SendResultMessageAsync(outcome, representativeJpeg, ct).ConfigureAwait(false);

        _pendingSave = new SaveInspectionRequest(
            _stateMachine.InspectionKey, DateTime.UtcNow, outcome.Result, outcome.Items,
            representativeJpeg, personConfidence, options.ModelName, options.ModelVersion);

        await PersistPendingSaveAsync(ct).ConfigureAwait(false);
        _analysisFrames.Clear();
    }

    private async Task PersistPendingSaveAsync(CancellationToken ct)
    {
        if (_pendingSave is null) return;

        using var scope = scopeFactory.CreateScope();
        var saveService = scope.ServiceProvider.GetRequiredService<InspectionSaveService>();
        var result = await saveService.SaveAsync(_pendingSave, ct).ConfigureAwait(false);

        if (result.Outcome == SaveOutcome.Success)
        {
            _stateMachine.MarkSaveSucceeded();
            await SendAsync(MessageTypes.SaveResultAck, Guid.NewGuid(), new SaveResultAckPayload(SaveStatusCodes.Saved, null), ct).ConfigureAwait(false);
        }
        else
        {
            _stateMachine.MarkSaveFailed();
            await SendAsync(MessageTypes.SaveResultAck, Guid.NewGuid(),
                new SaveResultAckPayload(SaveStatusCodes.SaveFailed, "결과를 저장하지 못했습니다."), ct).ConfigureAwait(false);
        }
    }

    private Task SendResultMessageAsync(InspectionOutcome outcome, byte[]? representativeJpeg, CancellationToken ct)
    {
        var items = outcome.Items
            .Select(i => new EquipmentResultPayload(EquipmentClassMap.ToDbCode(i.Code), i.Status.ToDbCode(), i.Score))
            .ToList();
        var payload = new InspectionResultPayload(
            _stateMachine.InspectionKey.ToString(), outcome.Result.ToDbCode(), items,
            representativeJpeg is not null, SaveStatusCodes.Saving);

        return SendWithOptionalImageAsync(MessageTypes.InspectionResult, payload, representativeJpeg, ct);
    }

    private async Task SendWithOptionalImageAsync<T>(string type, T payload, byte[]? image, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid();
        await SendAsync(type, correlationId, payload, ct).ConfigureAwait(false);
        if (image is not null) await SendImageAsync(image, ct).ConfigureAwait(false);
    }

    private Task SendStateAsync(CancellationToken ct) => SendAsync(
        MessageTypes.InspectionStateChanged, Guid.NewGuid(),
        new InspectionStateChangedPayload(StateCode(_stateMachine.State), _stateMachine.GuidanceMessage, SaveStateCode(_stateMachine.SaveState)),
        ct);

    private async Task SendAsync<T>(string type, Guid correlationId, T payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ProtocolMessage.SendAsync(_stream, type, correlationId, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendImageAsync(byte[] jpeg, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ProtocolMessage.SendImageAsync(_stream, jpeg, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
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
