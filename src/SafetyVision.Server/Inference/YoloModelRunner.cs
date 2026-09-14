using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using SafetyVision.Core.Domain;

namespace SafetyVision.Server.Inference;

internal static class YoloModelRunner
{
    public static List<DetectedBox> Run(Mat source, InferenceSession session, string inputName, int inputSize,
        IReadOnlyDictionary<int, DetectedClass> classMap, Func<DetectedClass, float> thresholdFor,
        double nmsIouThreshold)
    {
        var letterbox = Letterbox.Apply(source, inputSize);
        using var canvas = letterbox.Image;
        using var rgb = new Mat();
        Cv2.CvtColor(canvas, rgb, ColorConversionCodes.BGR2RGB);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, BuildInputTensor(rgb, inputSize))
        };
        using var results = session.Run(inputs);
        var output = results.First().AsTensor<float>();
        var dims = output.Dimensions;
        if (dims.Length != 3 || dims[0] != 1)
            throw new InvalidDataException($"예상하지 못한 모델 출력 형태입니다: [{string.Join(",", dims.ToArray())}]");

        int numBoxes = dims[2];
        int numClasses = dims[1] - 4;
        if (numClasses <= 0)
            throw new InvalidDataException($"예상하지 못한 모델 출력 채널 수입니다: {dims[1]}");

        var span = ((DenseTensor<float>)output).Buffer.Span;
        var raw = new List<RawDetection>();
        for (int i = 0; i < numBoxes; i++)
        {
            float cx = span[i], cy = span[numBoxes + i];
            float width = span[2 * numBoxes + i], height = span[3 * numBoxes + i];
            int bestClass = -1;
            float bestScore = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                float score = span[(4 + c) * numBoxes + i];
                if (classMap.TryGetValue(c, out var maskClass)
                    && maskClass is DetectedClass.Mask or DetectedClass.NoMask
                    && score >= thresholdFor(maskClass))
                    raw.Add(ToRaw(maskClass, score, cx, cy, width, height));

                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestClass < 0 || !classMap.TryGetValue(bestClass, out var detectedClass)) continue;
            if (detectedClass is DetectedClass.Mask or DetectedClass.NoMask) continue;
            if (bestScore >= thresholdFor(detectedClass))
                raw.Add(ToRaw(detectedClass, bestScore, cx, cy, width, height));
        }

        return ToOriginalCoordinates(
            NonMaxSuppression.ApplyPerClass(raw, nmsIouThreshold), letterbox, source.Width, source.Height);
    }

    private static RawDetection ToRaw(DetectedClass detectedClass, float score,
        float cx, float cy, float width, float height) =>
        new(detectedClass, score, cx - width / 2, cy - height / 2, cx + width / 2, cy + height / 2);

    private static List<DetectedBox> ToOriginalCoordinates(IReadOnlyList<RawDetection> detections,
        LetterboxResult letterbox, int sourceWidth, int sourceHeight)
    {
        var boxes = new List<DetectedBox>(detections.Count);
        foreach (var detection in detections)
        {
            var (x1, y1) = Letterbox.InverseTransform(detection.X1, detection.Y1, letterbox);
            var (x2, y2) = Letterbox.InverseTransform(detection.X2, detection.Y2, letterbox);
            x1 = Math.Clamp(x1, 0, sourceWidth);
            y1 = Math.Clamp(y1, 0, sourceHeight);
            x2 = Math.Clamp(x2, 0, sourceWidth);
            y2 = Math.Clamp(y2, 0, sourceHeight);
            boxes.Add(new DetectedBox(detection.Class, x1, y1, x2 - x1, y2 - y1, detection.Confidence));
        }
        return boxes;
    }

    private static DenseTensor<float> BuildInputTensor(Mat rgb, int inputSize)
    {
        int pixelCount = inputSize * inputSize;
        var pixelData = new byte[pixelCount * 3];
        Marshal.Copy(rgb.Data, pixelData, 0, pixelData.Length);
        var buffer = new float[3 * pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 3;
            buffer[i] = pixelData[offset] / 255f;
            buffer[pixelCount + i] = pixelData[offset + 1] / 255f;
            buffer[2 * pixelCount + i] = pixelData[offset + 2] / 255f;
        }
        return new DenseTensor<float>(buffer, [1, 3, inputSize, inputSize]);
    }
}
