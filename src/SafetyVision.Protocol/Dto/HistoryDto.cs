namespace SafetyVision.Protocol.Dto;

public sealed record HistoryPageRequestPayload(int Page, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? ResultFilter);

public sealed record HistoryRowPayload(
    long Id,
    DateTimeOffset InspectedAtUtc,
    string Hardhat,
    string Vest,
    string Mask,
    string Result);

public sealed record HistoryPageResponsePayload(int Page, int TotalPages, int TotalCount, IReadOnlyList<HistoryRowPayload> Rows);

public sealed record InspectionDetailRequestPayload(long Id);

public sealed record InspectionDetailResponsePayload(
    long Id,
    DateTimeOffset InspectedAtUtc,
    IReadOnlyList<EquipmentResultPayload> Items,
    string Result,
    bool ImageAvailable,
    string? ImageMissingMessage);
