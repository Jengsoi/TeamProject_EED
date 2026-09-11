using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Analysis;

// 임시 대체 로직: 지정된 모델(ayushgupta7777/safetyvision-yolov8)의 Person 클래스가 사실상 검출되지
// 않는 것으로 확인되어(실측: 여러 이미지에서 raw score 최댓값 0.0001 수준, 안전모/조끼는 정상 검출),
// SafetyVisionOptions.UsePpeAsPersonProxy=true일 때 PPE 박스 묶음으로 "사람이 있는 위치"를 추정한다.
// 05_AI모델명세.md는 Person 클래스 검출을 전제로 하므로, 이 로직은 문서 스펙에서 벗어난 임시 조치이며
// 실제 Person 검출이 가능한 모델을 구하면 옵션을 꺼서 즉시 원복할 수 있다.
public static class PersonProxyEstimator
{
    // 검출되는 PPE(안전모/마스크 등)는 대략 머리~허리 부근까지만 덮으므로, 전신 키는 그 대략 2배로 추정한다.
    private const float VisiblePortionOfBody = 0.5f;
    private const float ClusterMarginRatio = 0.6f;

    public static IReadOnlyList<DetectedBox> EstimatePersons(IReadOnlyList<DetectedBox> boxes, int frameHeight)
    {
        var ppe = boxes.Where(b => b.Class != DetectedClass.Person).ToList();
        if (ppe.Count == 0) return [];

        var clusters = ClusterByHorizontalProximity(ppe);
        var result = new List<DetectedBox>(clusters.Count);
        foreach (var cluster in clusters)
        {
            float minX = cluster.Min(b => b.X);
            float minY = cluster.Min(b => b.Y);
            float maxX = cluster.Max(b => b.X + b.Width);
            float maxY = cluster.Max(b => b.Y + b.Height);
            float width = maxX - minX;
            float visibleHeight = maxY - minY;
            float estimatedHeight = Math.Min(frameHeight - minY, visibleHeight / VisiblePortionOfBody);
            float avgConfidence = cluster.Average(b => b.Confidence);
            result.Add(new DetectedBox(DetectedClass.Person, minX, minY, width, Math.Max(estimatedHeight, visibleHeight), avgConfidence));
        }
        return result;
    }

    private static List<List<DetectedBox>> ClusterByHorizontalProximity(List<DetectedBox> boxes)
    {
        var sorted = boxes.OrderBy(b => b.X).ToList();
        var clusters = new List<List<DetectedBox>>();
        List<DetectedBox>? current = null;
        float currentMaxX = 0;

        foreach (var box in sorted)
        {
            float margin = box.Width * ClusterMarginRatio;
            if (current is not null && box.X <= currentMaxX + margin)
            {
                current.Add(box);
                currentMaxX = Math.Max(currentMaxX, box.X + box.Width);
            }
            else
            {
                current = [box];
                clusters.Add(current);
                currentMaxX = box.X + box.Width;
            }
        }
        return clusters;
    }
}
