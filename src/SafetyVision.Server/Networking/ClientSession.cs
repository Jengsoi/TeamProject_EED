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
    private readonly InspectionStateMachine _stateMachine = new(options);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // _stateMachine/_lastDetection/_lastFrameJpeg/_analysisFrames/_pendingSave는 원래 단일 수신 루프에서만
    // 건드렸지만, 프레임 처리를 별도 백그라운드 루프로 분리하면서(아래 참고) 다른 요청 처리(재시도 등)와
    // 동시에 접근될 수 있다. 이 락으로 두 쪽 모두를 보호한다.
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<AnalysisFrameRecord> _analysisFrames = [];

    // 프레임 처리(추론)가 전송 속도를 못 따라가도 지연이 계속 누적되지 않도록, 항상 "가장 최근 프레임 1개"만
    // 유지한다. 처리 중에 새 프레임이 도착하면 처리 대기 중이던 이전 프레임은 버려진다(DropOldest).
    private readonly Channel<QueuedFrame> _frameChannel =
        Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private DetectionFrame? _lastDetection;
    private byte[]? _lastFrameJpeg;
    private SaveInspectionRequest? _pendingSave;
    private string _cameraName = "CAM 01";
    private bool _isAuthenticated;
    private bool _inspectionSessionActive;
    private long _sessionGeneration;
    private long? _lastAcceptedSequenceNumber;
    private TaskCompletionSource<bool> _sessionChanged = CreateSessionChangedSignal();

    private sealed record AnalysisFrameRecord(byte[] Jpeg, IReadOnlyList<DetectedBox> Boxes, double PersonConfidence);
    private sealed record QueuedFrame(Envelope Envelope, byte[] ImageBytes, long SessionGeneration, long SequenceNumber);

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
            await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _inspectionSessionActive = false;
                AdvanceSessionGenerationLocked();
                _stateMachine.Cancel(CancelReason.ConnectionClosed, NowSeconds());
            }
            finally { _stateLock.Release(); }

            _frameChannel.Writer.TryComplete();
            try { await frameProcessingTask.ConfigureAwait(false); }
            catch (Exception) { /* 프레임 처리 루프 종료 중 예외는 연결 종료 처리에 영향 주지 않는다 */ }

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
        try
        {
            _inspectionSessionActive = false;
            AdvanceSessionGenerationLocked();
            _stateMachine.Cancel(CancelReason.LoggedOut, NowSeconds());
        }
        finally { _stateLock.Release(); }
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
                string.IsNullOrEmpty(r.CameraName) ? "CAM 01" : r.CameraName,
                r.Hardhat, r.Vest, r.Mask, r.Result, r.HasImage)).ToList());

        await SendAsync(MessageTypes.DashboardStatsResponse, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleStatisticsAsync(Envelope envelope, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dashboardSvc = scope.ServiceProvider.GetRequiredService<DashboardQueryService>();
        var statsSvc = scope.ServiceProvider.GetRequiredService<StatisticsQueryService>();

        var stats = await dashboardSvc.GetStatsAsync(ct).ConfigureAwait(false);
        var breakdown = await statsSvc.GetEquipmentBreakdownAsync(ct).ConfigureAwait(false);
        var dailyTrend = await statsSvc.GetDailyTrendAsync(14, ct).ConfigureAwait(false);
        var monthlyTrend = await statsSvc.GetMonthlyTrendAsync(6, ct).ConfigureAwait(false);
        var cameraBreakdown = await statsSvc.GetCameraBreakdownAsync(ct).ConfigureAwait(false);

        var payload = new StatisticsResponsePayload(
            stats.Total, stats.Normal, stats.CheckRequired + stats.Unconfirmed,
            breakdown.Select(b => new EquipmentBreakdownPayload(EquipmentClassMap.ToDbCode(b.Code), b.Worn, b.NotWorn, b.Unknown)).ToList(),
            dailyTrend.Select(d => new DailyTrendPointPayload(d.Date, d.Normal, d.CheckRequired)).ToList(),
            monthlyTrend.Select(m => new MonthlyTrendPointPayload(m.Year, m.Month, m.Normal, m.CheckRequired)).ToList(),
            cameraBreakdown.Select(c => new CameraBreakdownPayload(c.CameraName, c.Total, c.Normal, c.CheckRequired)).ToList());

        await SendAsync(MessageTypes.StatisticsResponse, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleSessionStartAsync(Envelope envelope, CancellationToken ct)
    {
        var req = envelope.DeserializePayload<InspectionSessionStartPayload>();
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _stateMachine.DiscardAndReturnToWaiting();
            _analysisFrames.Clear();
            _lastFrameJpeg = null;
            _lastDetection = null;
            _pendingSave = null;
            _lastAcceptedSequenceNumber = null;
            if (!string.IsNullOrWhiteSpace(req.CameraName))
                _cameraName = req.CameraName;
            _inspectionSessionActive = true;
            AdvanceSessionGenerationLocked();
        }
        finally { _stateLock.Release(); }

        var payload = new InspectionSessionStartedPayload(true, null, options.RoiLeft, options.RoiTop, options.RoiRight, options.RoiBottom);
        await SendAsync(MessageTypes.InspectionSessionStarted, envelope.CorrelationId, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleSessionEndAsync(Envelope envelope, CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _inspectionSessionActive = false;
            AdvanceSessionGenerationLocked();
            _stateMachine.Cancel(CancelReason.ScreenLeft, NowSeconds());
            _analysisFrames.Clear();
            _lastFrameJpeg = null;
            _lastDetection = null;
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

        var meta = envelope.DeserializePayload<FrameMetaPayload>();
        long sessionGeneration;
        bool sessionActive;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            sessionActive = _inspectionSessionActive;
            if (!sessionActive)
            {
                sessionGeneration = 0;
            }
            else if (_lastAcceptedSequenceNumber is long lastSequence
                && meta.SequenceNumber <= lastSequence)
            {
                return;
            }
            else
            {
                _lastAcceptedSequenceNumber = meta.SequenceNumber;
                _lastFrameJpeg = imageBytes;
                sessionGeneration = _sessionGeneration;
            }
        }
        finally { _stateLock.Release(); }

        if (!sessionActive)
        {
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.InvalidRequest,
                "검사 세션을 먼저 시작해 주세요.", ct).ConfigureAwait(false);
            return;
        }

        if (!detector.IsAvailable)
        {
            await TrySendErrorAsync(envelope.CorrelationId, ErrorCodes.ModelUnavailable,
                detector.UnavailableReason ?? "모델을 불러올 수 없습니다. 모델 파일을 확인해 주세요.", ct).ConfigureAwait(false);
            return;
        }

        // 용량 1 + DropOldest라 항상 즉시 반환되며, 처리 대기 중이던 이전 프레임이 있었다면 버려진다.
        await _frameChannel.Writer.WriteAsync(
            new QueuedFrame(envelope, imageBytes, sessionGeneration, meta.SequenceNumber), ct).ConfigureAwait(false);
    }

    // 채널에서 "가장 최근 프레임"을 하나씩 꺼내 실제 추론·상태 갱신을 수행하는 백그라운드 루프.
    // 추론이 전송 속도보다 느려도, 밀린 프레임을 순서대로 다 처리하는 게 아니라 최신 것만 처리하므로
    // 지연이 계속 누적되지 않는다.
    private async Task ProcessFramesAsync(CancellationToken ct)
    {
        double minInferenceInterval = 1.0 / options.MaxInferenceFps;
        long? lastInferenceGeneration = null;
        double? lastInferenceStartedAt = null;

        try
        {
            await foreach (var initialFrame in _frameChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var frame = initialFrame;
                try
                {
                    if (!await IsCurrentSessionAsync(frame.SessionGeneration, ct).ConfigureAwait(false))
                        continue;

                    if (lastInferenceGeneration != frame.SessionGeneration)
                    {
                        lastInferenceGeneration = frame.SessionGeneration;
                        lastInferenceStartedAt = null;
                    }

                    if (lastInferenceStartedAt is double previousInferenceStartedAt
                        && !await WaitForInferenceSlotAsync(
                            frame.SessionGeneration, previousInferenceStartedAt, minInferenceInterval, ct).ConfigureAwait(false))
                    {
                        continue;
                    }

                    while (_frameChannel.Reader.TryRead(out var newerFrame))
                        frame = newerFrame;

                    if (!await IsCurrentSessionAsync(frame.SessionGeneration, ct).ConfigureAwait(false))
                        continue;

                    if (lastInferenceGeneration != frame.SessionGeneration)
                    {
                        lastInferenceGeneration = frame.SessionGeneration;
                        lastInferenceStartedAt = null;
                    }

                    var inferenceStartedAt = await ProcessFrameAsync(frame, ct).ConfigureAwait(false);
                    if (inferenceStartedAt is double startedAt)
                    {
                        lastInferenceGeneration = frame.SessionGeneration;
                        lastInferenceStartedAt = startedAt;
                    }
                }
                catch (Exception ex) when (ex is not (IOException or EndOfStreamException or OperationCanceledException))
                {
                    logger.LogError(ex, "프레임 처리 중 오류");
                    await TrySendErrorAsync(frame.Envelope.CorrelationId, ErrorCodes.InvalidRequest, "요청을 처리하지 못했습니다.", ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 서버/연결 종료로 인한 정상적인 취소.
        }
    }

    private async Task<double?> ProcessFrameAsync(QueuedFrame frame, CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsCurrentSessionLocked(frame.SessionGeneration))
                return null;
        }
        finally { _stateLock.Release(); }

        double inferenceStartedAt = NowSeconds();

        DetectionFrame detection;
        try
        {
            detection = detector.Detect(frame.ImageBytes);
        }
        catch (Exception ex)
        {
            bool sessionStillActive;
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                sessionStillActive = IsCurrentSessionLocked(frame.SessionGeneration);
                if (sessionStillActive)
                {
                    _stateMachine.Cancel(CancelReason.InferenceError, NowSeconds());
                    _analysisFrames.Clear();
                }
            }
            finally { _stateLock.Release(); }

            if (!sessionStillActive)
                return inferenceStartedAt;

            logger.LogError(ex, "추론 실패");
            await TrySendErrorAsync(frame.Envelope.CorrelationId, ErrorCodes.InferenceError, "AI 분석 중 오류가 발생했습니다. 화면에 다시 진입해 주세요.", ct).ConfigureAwait(false);
            await SendStateAsync(ct).ConfigureAwait(false);
            return inferenceStartedAt;
        }

        var eval = FrameAnalyzer.Evaluate(detection.Boxes, detection.Width, detection.Height, options);

        logger.LogDebug(
        "검출={Boxes}",
        string.Join(", ",
            detection.Boxes.Select(x =>
                $"{x.Class}({x.Confidence:F2})")));

        bool justCompleted;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsCurrentSessionLocked(frame.SessionGeneration))
                return inferenceStartedAt;

            _lastDetection = detection;
            bool wasInspecting = _stateMachine.State == InspectionState.Inspecting;
            int beforeCount = _stateMachine.FrameVotes.Count;
            int beforeGeneration = _stateMachine.Generation;
            justCompleted = _stateMachine.ProcessFrame(eval, NowSeconds());

            if (_stateMachine.Generation != beforeGeneration)
                _analysisFrames.Clear();

            if (wasInspecting && eval.Condition == PersonRoiCondition.Qualified && _stateMachine.FrameVotes.Count > beforeCount)
            {
                var target = FrameAnalyzer.FindSingleRoiPerson(detection.Boxes, detection.Width, detection.Height, options);
                _analysisFrames.Add(new AnalysisFrameRecord(frame.ImageBytes, detection.Boxes, target?.Confidence ?? 0));
            }
        }
        finally { _stateLock.Release(); }

        if (justCompleted)
            await OnInspectionCompletedAsync(frame.SessionGeneration, ct).ConfigureAwait(false);
        else if (await IsCurrentSessionAsync(frame.SessionGeneration, ct).ConfigureAwait(false))
            await SendStateAsync(ct).ConfigureAwait(false);

        return inferenceStartedAt;
    }

    private async Task<bool> IsCurrentSessionAsync(long sessionGeneration, CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try { return IsCurrentSessionLocked(sessionGeneration); }
        finally { _stateLock.Release(); }
    }

    private async Task<bool> WaitForInferenceSlotAsync(
        long sessionGeneration,
        double lastInferenceStartedAt,
        double minInferenceInterval,
        CancellationToken ct)
    {
        while (true)
        {
            double remainingSeconds = minInferenceInterval - (NowSeconds() - lastInferenceStartedAt);
            if (remainingSeconds <= 0)
                return await IsCurrentSessionAsync(sessionGeneration, ct).ConfigureAwait(false);

            Task sessionChangedTask;
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!IsCurrentSessionLocked(sessionGeneration))
                    return false;
                sessionChangedTask = _sessionChanged.Task;
            }
            finally { _stateLock.Release(); }

            var delayTask = Task.Delay(TimeSpan.FromSeconds(remainingSeconds), ct);
            var completedTask = await Task.WhenAny(delayTask, sessionChangedTask).ConfigureAwait(false);
            if (completedTask == sessionChangedTask)
            {
                ct.ThrowIfCancellationRequested();
                return false;
            }

            await delayTask.ConfigureAwait(false);
            if (!await IsCurrentSessionAsync(sessionGeneration, ct).ConfigureAwait(false))
                return false;
        }
    }

    private bool IsCurrentSessionLocked(long sessionGeneration) =>
        _inspectionSessionActive && _sessionGeneration == sessionGeneration;

    private void AdvanceSessionGenerationLocked()
    {
        _sessionGeneration++;
        var previousSessionChanged = _sessionChanged;
        _sessionChanged = CreateSessionChangedSignal();
        previousSessionChanged.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CreateSessionChangedSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task HandleRetryInspectionAsync(Envelope envelope, CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lastDetection is not null)
            {
                var eval = FrameAnalyzer.Evaluate(_lastDetection.Boxes, _lastDetection.Width, _lastDetection.Height, options);
                if (_stateMachine.TryRetry(eval, NowSeconds()))
                    _analysisFrames.Clear();
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
                && key == _stateMachine.InspectionKey
                && _pendingSave is not null
                && _stateMachine.TryRetrySave();
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

        await SendWithOptionalImageAsync(
            MessageTypes.InspectionDetailResponse, envelope.CorrelationId, payload, imageBytes, ct).ConfigureAwait(false);
    }

    private async Task OnInspectionCompletedAsync(long sessionGeneration, CancellationToken ct)
    {
        byte[]? representativeJpeg;
        double? personConfidence;
        InspectionOutcome outcome;
        Guid inspectionKey;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stateMachine.Outcome is null
                || (_inspectionSessionActive && _sessionGeneration != sessionGeneration))
            {
                return;
            }

            outcome = _stateMachine.Outcome!;
            inspectionKey = _stateMachine.InspectionKey;
            personConfidence = null;
            representativeJpeg = null;

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

            _pendingSave = new SaveInspectionRequest(
                inspectionKey, DateTime.UtcNow, outcome.Result, outcome.Items,
                representativeJpeg, personConfidence, _cameraName, options.ModelName, options.ModelVersion);
            _analysisFrames.Clear();
        }
        finally { _stateLock.Release(); }

        // 저장은 클라이언트 연결 상태와 무관하게 먼저 끝낸다. 결과 전송이 실패해도 이력은 남는다.
        await PersistPendingSaveAsync(ct, notifyClient: false).ConfigureAwait(false);

        string saveState;
        bool shouldSendResult;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            shouldSendResult = _stateMachine.Outcome is not null
                && (!_inspectionSessionActive || _sessionGeneration == sessionGeneration);
            saveState = SaveStateCode(_stateMachine.SaveState);
        }
        finally { _stateLock.Release(); }

        if (!shouldSendResult)
            return;

        await SendResultMessageAsync(inspectionKey, outcome, representativeJpeg, saveState, ct).ConfigureAwait(false);
    }

    private async Task PersistPendingSaveAsync(CancellationToken ct, bool notifyClient = true)
    {
        SaveInspectionRequest? pending;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try { pending = _pendingSave; }
        finally { _stateLock.Release(); }

        if (pending is null) return;

        using var scope = scopeFactory.CreateScope();
        var saveService = scope.ServiceProvider.GetRequiredService<InspectionSaveService>();
        var result = await saveService.SaveAsync(pending, ct).ConfigureAwait(false);

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (result.Outcome == SaveOutcome.Success) _stateMachine.MarkSaveSucceeded();
            else _stateMachine.MarkSaveFailed();
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
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ProtocolMessage.SendAsync(_stream, type, correlationId, payload, ct).ConfigureAwait(false);
            if (image is not null)
                await ProtocolMessage.SendImageAsync(_stream, image, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendStateAsync(CancellationToken ct)
    {
        InspectionState state;
        string guidance;
        SaveState saveState;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            state = _stateMachine.State;
            guidance = _stateMachine.GuidanceMessage;
            saveState = _stateMachine.SaveState;
        }
        finally { _stateLock.Release(); }

        await SendAsync(
            MessageTypes.InspectionStateChanged, Guid.NewGuid(),
            new InspectionStateChangedPayload(StateCode(state), guidance, SaveStateCode(saveState)),
            ct).ConfigureAwait(false);
    }

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
