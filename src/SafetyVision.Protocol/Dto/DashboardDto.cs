namespace SafetyVision.Protocol.Dto;

public sealed record DashboardStatsRequestPayload;

public sealed record DashboardStatsResponsePayload(
    int Total,
    int Normal,
    int CheckRequired,
    int Unconfirmed,
    IReadOnlyList<EquipmentRatePayload> EquipmentRates,
    IReadOnlyList<RecentInspectionPayload> Recent);

// WornRatio는 0~1, 해당 장비 전체 검사 건수가 0이면 null('—').
public sealed record EquipmentRatePayload(string EquipmentCode, double? WornRatio);

public sealed record RecentInspectionPayload(
    long Id,
    DateTimeOffset InspectedAtUtc,
    string Camera,
    string Hardhat,
    string Vest,
    string Mask,
    string Result,
    bool HasImage);
