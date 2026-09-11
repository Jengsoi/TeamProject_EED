namespace SafetyVision.Protocol.Dto;

public sealed record InspectionSessionStartPayload(int ReportedWidth, int ReportedHeight, string CameraName);

public sealed record InspectionSessionStartedPayload(
    bool Success,
    string? ErrorMessage,
    double RoiLeft,
    double RoiTop,
    double RoiRight,
    double RoiBottom);

// 클라이언트→서버, 0x02 이미지 프레임 직전에 전송. correlationId로 뒤따르는 이미지와 매칭한다.
public sealed record FrameMetaPayload(long SequenceNumber, DateTimeOffset CapturedAtUtc, int Width, int Height);

public sealed record InspectionStateChangedPayload(string State, string Guidance, string SaveState);

public sealed record EquipmentResultPayload(string EquipmentCode, string Status, double? Score);

// 서버→클라이언트. imageAvailable=true면 같은 correlationId로 0x02 프레임이 곧바로 뒤따른다.
public sealed record InspectionResultPayload(
    string InspectionKey,
    string Result,
    IReadOnlyList<EquipmentResultPayload> Items,
    bool ImageAvailable,
    string SaveState);

public sealed record RetryInspectionRequestPayload;

public static class SaveStatusCodes
{
    public const string Saving = "SAVING";
    public const string Saved = "SAVED";
    public const string SaveFailed = "SAVE_FAILED";
}

public sealed record SaveResultAckPayload(string Status, string? Message);

public sealed record RetrySaveRequestPayload(string InspectionKey);

public sealed record InspectionSessionEndPayload(string? Reason);
