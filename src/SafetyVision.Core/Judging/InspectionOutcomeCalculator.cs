using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Judging;

public static class InspectionOutcomeCalculator
{
    public static InspectionResultType Combine(IReadOnlyList<EquipmentJudgement> items)
    {
        if (items.All(i => i.Status == EquipmentStatus.Worn)) return InspectionResultType.Normal;
        if (items.Any(i => i.Status == EquipmentStatus.NotWorn)) return InspectionResultType.CheckRequired;
        return InspectionResultType.Unconfirmed;
    }
}
