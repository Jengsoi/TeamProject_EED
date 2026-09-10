namespace SafetyVision.Core.Domain;

public sealed record EquipmentJudgement(
    EquipmentCode Code,
    EquipmentStatus Status,
    double? Score,
    int Positive,
    int Negative,
    int Total);

public sealed record InspectionOutcome(
    InspectionResultType Result,
    IReadOnlyList<EquipmentJudgement> Items,
    int RepresentativeFrameIndex);
