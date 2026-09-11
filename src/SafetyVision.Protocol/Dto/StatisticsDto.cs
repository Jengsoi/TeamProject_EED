namespace SafetyVision.Protocol.Dto;

public sealed record StatisticsRequestPayload;

public sealed record EquipmentBreakdownPayload(string EquipmentCode, int Worn, int NotWorn, int Unknown);

public sealed record StatisticsResponsePayload(
    int Total,
    int Normal,
    int CheckRequired,
    IReadOnlyList<EquipmentBreakdownPayload> Breakdown);
