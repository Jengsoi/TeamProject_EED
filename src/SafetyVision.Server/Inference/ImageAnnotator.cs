using OpenCvSharp;
using SafetyVision.Core.Domain;

namespace SafetyVision.Server.Inference;

// 04_DB설계.md 6절: 대표 프레임의 실제 박스와 클래스명만 그린다. 가상 박스·다른 프레임 박스를 합성하지 않는다.
public static class ImageAnnotator
{
    public static byte[] DrawBoxes(byte[] sourceJpeg, IEnumerable<DetectedBox> boxes, int jpegQuality)
    {
        using var mat = Cv2.ImDecode(sourceJpeg, ImreadModes.Color);
        foreach (var box in boxes)
        {
            if (box.Class == DetectedClass.Person) continue;

            var rect = new Rect((int)box.X, (int)box.Y, (int)Math.Max(1, box.Width), (int)Math.Max(1, box.Height));
            var color = IsPositiveClass(box.Class) ? new Scalar(0, 170, 0) : new Scalar(0, 120, 255);
            Cv2.Rectangle(mat, rect, color, 2);
            Cv2.PutText(mat, ClassLabel(box.Class), new Point(rect.X, Math.Max(15, rect.Y - 6)),
                HersheyFonts.HersheySimplex, 0.6, color, 2);
        }

        Cv2.ImEncode(".jpg", mat, out var bytes, [new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality)]);
        return bytes;
    }

    private static bool IsPositiveClass(DetectedClass c) =>
        c is DetectedClass.Hardhat or DetectedClass.Mask or DetectedClass.SafetyVest;

    private static string ClassLabel(DetectedClass c) => c switch
    {
        DetectedClass.Hardhat => "Hardhat",
        DetectedClass.NoHardhat => "NO-Hardhat",
        DetectedClass.Mask => "Mask",
        DetectedClass.NoMask => "NO-Mask",
        DetectedClass.SafetyVest => "Safety Vest",
        DetectedClass.NoSafetyVest => "NO-Safety Vest",
        _ => c.ToString()
    };
}
