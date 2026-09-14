using OpenCvSharp;

namespace SafetyVision.Server.Inference;

public readonly record struct LetterboxResult(Mat Image, double Scale, int PadLeft, int PadTop);

// 05_AI모델명세.md 3절: 원본 비율을 유지해 targetSize x targetSize 안에 맞추고 남는 영역을 값 114로 중앙 패딩한다.
// targetSize는 모델의 입력 해상도(SafetyVisionOptions.ModelInputSize)에 맞춰 호출자가 지정한다.
public static class Letterbox
{
    public const byte PadValue = 114;

    public static LetterboxResult Apply(Mat source, int targetSize)
    {
        double scale = Math.Min((double)targetSize / source.Width, (double)targetSize / source.Height);
        int newWidth = (int)Math.Round(source.Width * scale);
        int newHeight = (int)Math.Round(source.Height * scale);

        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(newWidth, newHeight));

        int padWidth = targetSize - newWidth;
        int padHeight = targetSize - newHeight;
        int left = padWidth / 2;
        int right = padWidth - left;
        int top = padHeight / 2;
        int bottom = padHeight - top;

        var canvas = new Mat();
        Cv2.CopyMakeBorder(resized, canvas, top, bottom, left, right, BorderTypes.Constant,
            new Scalar(PadValue, PadValue, PadValue));

        return new LetterboxResult(canvas, scale, left, top);
    }

    public static (float X, float Y) InverseTransform(float x640, float y640, LetterboxResult letterbox) =>
        ((float)((x640 - letterbox.PadLeft) / letterbox.Scale), (float)((y640 - letterbox.PadTop) / letterbox.Scale));
}
