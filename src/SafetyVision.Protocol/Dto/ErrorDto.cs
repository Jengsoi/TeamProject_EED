namespace SafetyVision.Protocol.Dto;

public static class ErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InferenceError = "INFERENCE_ERROR";
    public const string ModelUnavailable = "MODEL_UNAVAILABLE";
    public const string DatabaseError = "DATABASE_ERROR";
}

public sealed record ErrorNotificationPayload(string Code, string Message);
