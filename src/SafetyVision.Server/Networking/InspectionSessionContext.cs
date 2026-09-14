using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using SafetyVision.Core.StateMachine;
using SafetyVision.Data.Services;
using SafetyVision.Server.Inference;

namespace SafetyVision.Server.Networking;

internal sealed class InspectionSessionContext(SafetyVisionOptions options)
{
    public InspectionStateMachine StateMachine { get; } = new(options);
    public List<AnalysisFrameRecord> AnalysisFrames { get; } = [];
    public DetectionFrame? LastDetection { get; set; }
    public byte[]? LastFrameJpeg { get; set; }
    public SaveInspectionRequest? PendingSave { get; set; }
    public string CameraName { get; set; } = "CAM 01";

    public void ClearFrames()
    {
        AnalysisFrames.Clear();
        LastFrameJpeg = null;
        LastDetection = null;
    }
}

internal sealed record AnalysisFrameRecord(
    byte[] Jpeg,
    IReadOnlyList<DetectedBox> Boxes,
    double PersonConfidence);
