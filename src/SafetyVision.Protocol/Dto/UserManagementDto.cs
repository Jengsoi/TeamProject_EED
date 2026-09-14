namespace SafetyVision.Protocol.Dto;

public sealed record UserListRequestPayload;

public sealed record UserSummaryPayload(long Id, string LoginId, string Name, DateTimeOffset CreatedAtUtc);

public sealed record UserListResponsePayload(IReadOnlyList<UserSummaryPayload> Users);

public sealed record UserCreateRequestPayload(string LoginId, string Name, string Password);

public sealed record UserPasswordChangeRequestPayload(long Id, string NewPassword);

public sealed record UserDeleteRequestPayload(long Id);

public sealed record UserMutationResponsePayload(bool Success, string? Message, long? Id);
