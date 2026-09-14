namespace SafetyVision.Protocol.Dto;

public sealed record StatisticsRequestPayload(DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null);

public sealed record EquipmentBreakdownPayload(string EquipmentCode, int Worn, int NotWorn, int Unknown);

public sealed record DailyTrendPointPayload(DateOnly Date, int Normal, int CheckRequired);

public sealed record MonthlyTrendPointPayload(int Year, int Month, int Normal, int CheckRequired);

public sealed record CameraBreakdownPayload(string CameraName, int Total, int Normal, int CheckRequired);

public sealed record StatisticsResponsePayload(
    int Total,
    int Normal,
    int CheckRequired,
    IReadOnlyList<EquipmentBreakdownPayload> Breakdown,
    IReadOnlyList<DailyTrendPointPayload> DailyTrend,
    IReadOnlyList<MonthlyTrendPointPayload> MonthlyTrend,
    IReadOnlyList<CameraBreakdownPayload> CameraBreakdown,
    double? NormalRatio = null,
    DateTimeOffset? FirstInspectedAtUtc = null,
    DateTimeOffset? LastInspectedAtUtc = null);
