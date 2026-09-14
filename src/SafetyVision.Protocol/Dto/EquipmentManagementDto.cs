namespace SafetyVision.Protocol.Dto;

public sealed record EquipmentManagementRequestPayload;

public sealed record EquipmentRulePayload(
    string EquipmentCode,
    string DisplayName,
    IReadOnlyList<string> PositiveModelClasses,
    IReadOnlyList<string> NegativeModelClasses,
    double RegionTopRatio,
    double RegionBottomRatio,
    double HorizontalMarginRatio);

public sealed record EquipmentUsagePayload(
    string EquipmentCode,
    int Worn,
    int NotWorn,
    int Unknown,
    int Total,
    double? WornRatio,
    double? AverageScore);

public sealed record EquipmentManagementResponsePayload(
    double DetectionConfidence,
    double NmsIouThreshold,
    int MinEvidenceFrames,
    double MinEvidenceRatio,
    double DecisionRatio,
    int MinAnalysisFrames,
    int TargetAnalysisFrames,
    string ModelName,
    string ModelVersion,
    IReadOnlyList<EquipmentRulePayload> Rules,
    IReadOnlyList<EquipmentUsagePayload> Usage);
