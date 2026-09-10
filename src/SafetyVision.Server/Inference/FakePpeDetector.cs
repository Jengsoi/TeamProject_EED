using OpenCvSharp;
using SafetyVision.Core.Domain;

namespace SafetyVision.Server.Inference;

// FR-03: 서버 개발 설정에서 명시적으로 선택할 때만 사용한다. 실제 모델 실패 시 자동 전환 금지.
public sealed class FakePpeDetector : IPpeDetector
{
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public bool IsFakeMode => true;

    public DetectionFrame Detect(ReadOnlySpan<byte> jpegBytes)
    {
        using var mat = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
        int width = mat.Empty() ? 1280 : mat.Width;
        int height = mat.Empty() ? 720 : mat.Height;

        float pw = width * 0.3f, ph = height * 0.6f;
        float px = (width - pw) / 2f, py = height * 0.2f;

        var boxes = new List<DetectedBox>
        {
            new(DetectedClass.Person, px, py, pw, ph, 0.95f),
            new(DetectedClass.Hardhat, px + pw * 0.2f, py, pw * 0.6f, ph * 0.2f, 0.9f),
            new(DetectedClass.SafetyVest, px, py + ph * 0.3f, pw, ph * 0.4f, 0.9f),
            new(DetectedClass.Mask, px + pw * 0.25f, py + ph * 0.1f, pw * 0.5f, ph * 0.2f, 0.85f),
        };
        return new DetectionFrame(boxes, width, height);
    }
}
