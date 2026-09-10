using SafetyVision.Core.Domain;

namespace SafetyVision.Server.Inference;

public readonly record struct RawDetection(DetectedClass Class, float Confidence, float X1, float Y1, float X2, float Y2)
{
    public float Area => Math.Max(0, X2 - X1) * Math.Max(0, Y2 - Y1);
}

public static class NonMaxSuppression
{
    // 클래스별로 그룹지어 적용한다: 서로 다른 착용/미착용 클래스를 NMS로 서로 지우지 않는다.
    public static List<RawDetection> ApplyPerClass(IEnumerable<RawDetection> detections, double iouThreshold)
    {
        var result = new List<RawDetection>();
        foreach (var group in detections.GroupBy(d => d.Class))
        {
            result.AddRange(ApplySingleClass(group.ToList(), iouThreshold));
        }
        return result;
    }

    private static List<RawDetection> ApplySingleClass(List<RawDetection> boxes, double iouThreshold)
    {
        var ordered = boxes.OrderByDescending(b => b.Confidence).ToList();
        var kept = new List<RawDetection>();
        var suppressed = new bool[ordered.Count];

        for (int i = 0; i < ordered.Count; i++)
        {
            if (suppressed[i]) continue;
            var current = ordered[i];
            kept.Add(current);
            for (int j = i + 1; j < ordered.Count; j++)
            {
                if (suppressed[j]) continue;
                if (Iou(current, ordered[j]) > iouThreshold) suppressed[j] = true;
            }
        }
        return kept;
    }

    private static float Iou(RawDetection a, RawDetection b)
    {
        float x1 = Math.Max(a.X1, b.X1);
        float y1 = Math.Max(a.Y1, b.Y1);
        float x2 = Math.Min(a.X2, b.X2);
        float y2 = Math.Min(a.Y2, b.Y2);
        float intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float union = a.Area + b.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
