using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Judging;

// 임시 조치(팀 결정): 개별 장비 판정은 착용/미착용/미확인 3가지를 유지하되,
// 검사 전체의 최종 결과는 정상/점검 필요 2가지로만 단순화한다(Unconfirmed 최종 결과는 내보내지 않음).
// 미확인 장비가 하나라도 있으면(미착용이 없어도) 점검이 필요한 것으로 취급한다.
public static class InspectionOutcomeCalculator
{
    public static InspectionResultType Combine(IReadOnlyList<EquipmentJudgement> items) =>
        items.All(i => i.Status == EquipmentStatus.Worn) ? InspectionResultType.Normal : InspectionResultType.CheckRequired;
}
