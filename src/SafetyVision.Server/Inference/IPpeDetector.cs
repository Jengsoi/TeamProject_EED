using SafetyVision.Core.Domain;

namespace SafetyVision.Server.Inference;

public sealed record DetectionFrame(IReadOnlyList<DetectedBox> Boxes, int Width, int Height);

public interface IPpeDetector
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    bool IsFakeMode { get; }

    DetectionFrame Detect(ReadOnlySpan<byte> jpegBytes);
}
