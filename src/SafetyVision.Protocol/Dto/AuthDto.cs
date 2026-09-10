namespace SafetyVision.Protocol.Dto;

public sealed record LoginRequestPayload(string LoginId, string Password);

public sealed record LoginResponsePayload(bool Success, string? DisplayName, string? FailureMessage);

public sealed record LogoutRequestPayload;

public sealed record LogoutResponsePayload(bool Success);
