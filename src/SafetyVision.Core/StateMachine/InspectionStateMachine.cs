using SafetyVision.Core.Analysis;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using SafetyVision.Core.Judging;

namespace SafetyVision.Core.StateMachine;

// TCP 연결(세션) 하나가 소유하는 검사 상태 머신. 02_요구사항.md FR-05/FR-06, 05_AI모델명세.md 4·8절.
// 순수 로직: 네트워크·DB에 의존하지 않는다. 시간은 호출자가 전달하는 단조 증가 초 단위 값을 사용한다.
public sealed class InspectionStateMachine
{
    private readonly SafetyVisionOptions _options;
    private readonly Dictionary<EquipmentCode, EquipmentAccumulator> _accumulators = new();
    private readonly List<IReadOnlyDictionary<EquipmentCode, FrameVote>> _frameVotes = new();

    private double? _stableSince;
    private int _stableObservationCount;
    private double? _absentSince;
    private double _inspectionStartTime;

    public InspectionStateMachine(SafetyVisionOptions options)
    {
        _options = options;
        foreach (var pair in EquipmentClassMap.All) _accumulators[pair.Code] = new EquipmentAccumulator();
    }

    public InspectionState State { get; private set; } = InspectionState.Waiting;
    public SaveState SaveState { get; private set; } = SaveState.None;
    public bool IsErrorLocked { get; private set; }
    public string GuidanceMessage { get; private set; } = InspectionMessages.Waiting;
    public Guid InspectionKey { get; private set; }
    public int Generation { get; private set; }
    public InspectionOutcome? Outcome { get; private set; }
    public IReadOnlyList<IReadOnlyDictionary<EquipmentCode, FrameVote>> FrameVotes => _frameVotes;

    // true를 반환하면 이번 호출로 RESULT에 막 진입했다는 뜻이며, Server는 즉시 최초 저장을 시작해야 한다.
    public bool ProcessFrame(FrameEvaluation eval, double nowSeconds) => State switch
    {
        InspectionState.Waiting => ProcessWaiting(eval),
        InspectionState.PersonDetected => ProcessPersonDetected(eval, nowSeconds),
        InspectionState.Inspecting => ProcessInspecting(eval, nowSeconds),
        InspectionState.Result => ProcessResult(eval, nowSeconds),
        _ => false
    };

    public bool TryRetry(FrameEvaluation currentEval, double nowSeconds)
    {
        if (State != InspectionState.Result || SaveState != SaveState.Saved || IsErrorLocked) return false;
        if (currentEval.Condition != PersonRoiCondition.Qualified) return false;
        StartInspecting(nowSeconds);
        return true;
    }

    public void Cancel(CancelReason reason, double nowSeconds)
    {
        switch (State)
        {
            case InspectionState.PersonDetected:
            case InspectionState.Inspecting:
                CancelInternal(reason);
                break;
            case InspectionState.Result:
                if (reason is CancelReason.DeviceError or CancelReason.InferenceError or CancelReason.DatabaseError)
                    IsErrorLocked = true;
                else if (reason is CancelReason.ScreenLeft or CancelReason.LoggedOut or CancelReason.ConnectionClosed)
                    DiscardAndReturnToWaiting();
                // 화면 이동/로그아웃/연결 종료는 확정 결과를 더 이상 재사용하지 않도록 세션을 폐기한다.
                break;
        }
    }

    public void MarkSaveSucceeded()
    {
        if (State == InspectionState.Result) SaveState = SaveState.Saved;
    }

    public void MarkSaveFailed()
    {
        if (State == InspectionState.Result) SaveState = SaveState.Failed;
    }

    public bool TryRetrySave()
    {
        if (State != InspectionState.Result || SaveState != SaveState.Failed) return false;
        SaveState = SaveState.Saving;
        return true;
    }

    // 저장 실패 후 "대시보드로 돌아가기": 미저장 결과는 서버 메모리에서 폐기한다.
    public void DiscardAndReturnToWaiting()
    {
        State = InspectionState.Waiting;
        Generation++;
        ResetForNextWaiting();
    }

    private bool ProcessWaiting(FrameEvaluation eval)
    {
        switch (eval.Condition)
        {
            case PersonRoiCondition.Qualified:
                State = InspectionState.PersonDetected;
                // nowSeconds는 다음 프레임에서 갱신되므로 최초 관측 시각은 PersonDetected 첫 호출에서 기록한다.
                _stableSince = null;
                _stableObservationCount = 0;
                GuidanceMessage = InspectionMessages.StableDetected;
                break;
            case PersonRoiCondition.Multiple:
                GuidanceMessage = InspectionMessages.MultiplePersons;
                break;
            case PersonRoiCondition.TooSmall:
                GuidanceMessage = InspectionMessages.TooSmall;
                break;
            default:
                GuidanceMessage = InspectionMessages.Waiting;
                break;
        }
        return false;
    }

    private bool ProcessPersonDetected(FrameEvaluation eval, double now)
    {
        if (eval.Condition != PersonRoiCondition.Qualified)
        {
            State = InspectionState.Waiting;
            Generation++;
            _stableSince = null;
            _stableObservationCount = 0;
            GuidanceMessage = eval.Condition switch
            {
                PersonRoiCondition.Multiple => InspectionMessages.MultiplePersons,
                PersonRoiCondition.TooSmall => InspectionMessages.TooSmall,
                _ => InspectionMessages.Waiting
            };
            return false;
        }

        _stableSince ??= now;
        _stableObservationCount++;

        if (_stableObservationCount >= 2 && now - _stableSince.Value >= _options.PersonStableDurationSeconds)
        {
            StartInspecting(now);
        }
        else
        {
            GuidanceMessage = InspectionMessages.StableDetected;
        }
        return false;
    }

    private bool ProcessInspecting(FrameEvaluation eval, double now)
    {
        switch (eval.Condition)
        {
            case PersonRoiCondition.Multiple:
                CancelInternal(CancelReason.MultiplePersons);
                return false;

            case PersonRoiCondition.None:
                _absentSince ??= now;
                if (now - _absentSince.Value >= _options.PersonLeaveDurationSeconds)
                {
                    CancelInternal(CancelReason.PersonLeft);
                    return false;
                }
                break;

            case PersonRoiCondition.TooSmall:
                _absentSince = null;
                break;

            case PersonRoiCondition.Qualified:
                _absentSince = null;
                int currentN = _accumulators[EquipmentCode.Hardhat].Total;
                if (currentN < _options.TargetAnalysisFrames)
                {
                    foreach (var pair in EquipmentClassMap.All)
                    {
                        var vote = eval.Votes.TryGetValue(pair.Code, out var v) ? v : FrameVote.NoInfo;
                        _accumulators[pair.Code].Apply(vote);
                    }
                    _frameVotes.Add(eval.Votes);
                }
                break;
        }

        double elapsed = now - _inspectionStartTime;
        int n = _accumulators[EquipmentCode.Hardhat].Total;
        bool targetReached = elapsed >= _options.MinAnalysisDurationSeconds && n >= _options.TargetAnalysisFrames;
        bool maxReached = elapsed >= _options.MaxAnalysisDurationSeconds;
        if (targetReached || maxReached)
        {
            CompleteInspecting();
            return true;
        }
        return false;
    }

    private bool ProcessResult(FrameEvaluation eval, double now)
    {
        if (eval.Condition == PersonRoiCondition.None)
        {
            _absentSince ??= now;
            if (SaveState == SaveState.Saved && !IsErrorLocked
                && now - _absentSince.Value >= _options.PersonLeaveDurationSeconds)
            {
                DiscardAndReturnToWaiting();
            }
        }
        else
        {
            _absentSince = null;
        }
        return false;
    }

    private void StartInspecting(double now)
    {
        State = InspectionState.Inspecting;
        InspectionKey = Guid.NewGuid();
        Generation++;
        _inspectionStartTime = now;
        _absentSince = null;
        foreach (var acc in _accumulators.Values) acc.Reset();
        _frameVotes.Clear();
        Outcome = null;
        SaveState = SaveState.None;
        IsErrorLocked = false;
        GuidanceMessage = InspectionMessages.Inspecting;
    }

    private void CompleteInspecting()
    {
        var judgements = EquipmentClassMap.All
            .Select(p => EquipmentJudge.Judge(p.Code, _accumulators[p.Code], _options))
            .ToList();
        var result = InspectionOutcomeCalculator.Combine(judgements);
        int repIndex = RepresentativeFrameSelector.SelectIndex(_frameVotes);
        Outcome = new InspectionOutcome(result, judgements, repIndex);
        State = InspectionState.Result;
        SaveState = SaveState.Saving;
        _absentSince = null;
        GuidanceMessage = string.Empty;
    }

    private void CancelInternal(CancelReason reason)
    {
        State = InspectionState.Waiting;
        Generation++;
        _stableSince = null;
        _stableObservationCount = 0;
        _absentSince = null;
        foreach (var acc in _accumulators.Values) acc.Reset();
        _frameVotes.Clear();
        Outcome = null;
        SaveState = SaveState.None;
        IsErrorLocked = false;
        GuidanceMessage = reason == CancelReason.MultiplePersons
            ? InspectionMessages.MultiplePersons
            : InspectionMessages.Waiting;
    }

    private void ResetForNextWaiting()
    {
        _stableSince = null;
        _stableObservationCount = 0;
        _absentSince = null;
        _inspectionStartTime = 0;
        foreach (var acc in _accumulators.Values) acc.Reset();
        _frameVotes.Clear();
        Outcome = null;
        SaveState = SaveState.None;
        IsErrorLocked = false;
        GuidanceMessage = InspectionMessages.Waiting;
    }
}
