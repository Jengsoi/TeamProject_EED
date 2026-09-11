using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using SafetyVision.Core.StateMachine;
using Xunit;

namespace SafetyVision.Tests.Core;

public class InspectionStateMachineTests
{
    private static readonly IReadOnlyDictionary<EquipmentCode, FrameVote> AllPositive = new Dictionary<EquipmentCode, FrameVote>
    {
        [EquipmentCode.Hardhat] = FrameVote.Positive,
        [EquipmentCode.Vest] = FrameVote.Positive,
        [EquipmentCode.Mask] = FrameVote.Positive,
    };

    private static FrameEvaluation Qualified() => new(PersonRoiCondition.Qualified, AllPositive);

    [Fact]
    public void StableQualifiedFrames_TransitionsToInspecting()
    {
        var sm = new InspectionStateMachine(new SafetyVisionOptions());

        sm.ProcessFrame(Qualified(), 0.0);
        Assert.Equal(InspectionState.PersonDetected, sm.State);

        sm.ProcessFrame(Qualified(), 0.1);
        Assert.Equal(InspectionState.PersonDetected, sm.State);

        sm.ProcessFrame(Qualified(), 0.9); // 0.9-0.1=0.8초 유지, 2회째 관측
        Assert.Equal(InspectionState.Inspecting, sm.State);
    }

    [Fact]
    public void MultipleDuringInspecting_CancelsWithoutSaving()
    {
        var sm = new InspectionStateMachine(new SafetyVisionOptions());
        EnterInspecting(sm);

        sm.ProcessFrame(FrameEvaluation.Multiple, 1.0);

        Assert.Equal(InspectionState.Waiting, sm.State);
        Assert.Null(sm.Outcome);
    }

    [Fact]
    public void AbsentOneSecondDuringInspecting_Cancels()
    {
        var sm = new InspectionStateMachine(new SafetyVisionOptions());
        EnterInspecting(sm, startNow: 0.0);

        sm.ProcessFrame(FrameEvaluation.None, 0.2);
        Assert.Equal(InspectionState.Inspecting, sm.State); // 1초 미만은 유지

        sm.ProcessFrame(FrameEvaluation.None, 1.3); // 0.2부터 1.1초 경과
        Assert.Equal(InspectionState.Waiting, sm.State);
    }

    [Fact]
    public void ShortAbsence_DoesNotResetAccumulatedFrames()
    {
        var sm = new InspectionStateMachine(new SafetyVisionOptions());
        EnterInspecting(sm, startNow: 0.0);

        sm.ProcessFrame(Qualified(), 0.2);
        sm.ProcessFrame(FrameEvaluation.None, 0.5); // 1초 미만 이탈, 해당 프레임만 제외
        sm.ProcessFrame(Qualified(), 0.7);

        Assert.Equal(InspectionState.Inspecting, sm.State);
    }

    [Fact]
    public void ReachingTargetFramesAndMinDuration_CompletesWithOutcome()
    {
        var sm = new InspectionStateMachine(new SafetyVisionOptions());
        EnterInspecting(sm, startNow: 0.0);

        bool completedNow = false;
        for (int i = 1; i <= 12; i++)
        {
            completedNow = sm.ProcessFrame(Qualified(), i * 0.2); // 12번째 호출 시 now=2.4 (>=2.0)
        }

        Assert.True(completedNow);
        Assert.Equal(InspectionState.Result, sm.State);
        Assert.Equal(SaveState.Saving, sm.SaveState);
        Assert.NotNull(sm.Outcome);
        Assert.All(sm.Outcome!.Items, item => Assert.Equal(EquipmentStatus.Worn, item.Status));
        Assert.Equal(InspectionResultType.Normal, sm.Outcome!.Result);
    }

    [Fact]
    public void MaxDurationWithFewFrames_CompletesAsUnknownItemsButCheckRequiredOverall()
    {
        // 개별 장비는 미확인(Unknown)을 유지하지만, 최종 결과는 2가지(정상/점검 필요)로 단순화되어
        // "미확인 장비가 있으면 점검 필요"로 귀결된다(InspectionOutcomeCalculator 참고).
        var sm = new InspectionStateMachine(new SafetyVisionOptions());
        EnterInspecting(sm, startNow: 0.0);

        bool completedNow = sm.ProcessFrame(FrameEvaluation.TooSmall, 5.0);

        Assert.True(completedNow);
        Assert.Equal(InspectionState.Result, sm.State);
        Assert.All(sm.Outcome!.Items, item => Assert.Equal(EquipmentStatus.Unknown, item.Status));
        Assert.Equal(InspectionResultType.CheckRequired, sm.Outcome!.Result);
        Assert.Equal(-1, sm.Outcome!.RepresentativeFrameIndex);
    }

    [Fact]
    public void RetryAfterSaved_SkipsStableWaitAndStartsNewInspectionKey()
    {
        var sm = new InspectionStateMachine(new SafetyVisionOptions());
        EnterInspecting(sm, startNow: 0.0);
        for (int i = 1; i <= 12; i++) sm.ProcessFrame(Qualified(), i * 0.2);
        var firstKey = sm.InspectionKey;
        sm.MarkSaveSucceeded();

        bool retried = sm.TryRetry(Qualified(), 10.0);

        Assert.True(retried);
        Assert.Equal(InspectionState.Inspecting, sm.State);
        Assert.NotEqual(firstKey, sm.InspectionKey);
    }

    [Fact]
    public void RetryWithoutSavedResult_IsRejected()
    {
        var sm = new InspectionStateMachine(new SafetyVisionOptions());
        EnterInspecting(sm, startNow: 0.0);
        for (int i = 1; i <= 12; i++) sm.ProcessFrame(Qualified(), i * 0.2);
        // 저장 미완료(SaveState.Saving) 상태

        bool retried = sm.TryRetry(Qualified(), 10.0);

        Assert.False(retried);
        Assert.Equal(InspectionState.Result, sm.State);
    }

    [Fact]
    public void SaveFailure_AllowsRetrySaveOrDiscard()
    {
        var sm = new InspectionStateMachine(new SafetyVisionOptions());
        EnterInspecting(sm, startNow: 0.0);
        for (int i = 1; i <= 12; i++) sm.ProcessFrame(Qualified(), i * 0.2);

        sm.MarkSaveFailed();
        Assert.False(sm.TryRetry(Qualified(), 10.0)); // 저장 실패 중 재검사 잠금

        Assert.True(sm.TryRetrySave());
        Assert.Equal(SaveState.Saving, sm.SaveState);
    }

    [Fact]
    public void AutoReturnToWaiting_OnlyAfterSavedAndOneSecondAbsence()
    {
        var sm = new InspectionStateMachine(new SafetyVisionOptions());
        EnterInspecting(sm, startNow: 0.0);
        for (int i = 1; i <= 12; i++) sm.ProcessFrame(Qualified(), i * 0.2);
        sm.MarkSaveSucceeded();

        sm.ProcessFrame(FrameEvaluation.None, 3.0);
        Assert.Equal(InspectionState.Result, sm.State); // 1초 미만

        sm.ProcessFrame(FrameEvaluation.None, 4.1);
        Assert.Equal(InspectionState.Waiting, sm.State);
    }

    // startNow는 Inspecting 진입 시각(=세 번째 프레임 시각)이 된다. 앞의 두 프레임은 0.0/0.1초 간격을 유지한 채
    // startNow 기준으로 역산한 시각을 사용해 시간 단조 증가를 보장한다.
    private static void EnterInspecting(InspectionStateMachine sm, double startNow = 0.9)
    {
        double t0 = startNow - 0.9;
        sm.ProcessFrame(Qualified(), t0);
        sm.ProcessFrame(Qualified(), t0 + 0.1);
        sm.ProcessFrame(Qualified(), t0 + 0.9);
    }
}
