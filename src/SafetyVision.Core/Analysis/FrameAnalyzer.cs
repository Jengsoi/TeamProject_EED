using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Analysis;

// 한 프레임의 원본 좌표계 검출 결과를 ROI 인원 조건과 장비별 표(vote)로 해석하는 순수 함수.
// 네트워크·DB·시간(타이머)에 의존하지 않는다: 상태 머신이 호출해 결과만 소비한다.
public static class FrameAnalyzer
{
    public static FrameEvaluation Evaluate(
        IReadOnlyList<DetectedBox> boxes,
        int frameWidth,
        int frameHeight,
        SafetyVisionOptions options)
    {
        var persons = boxes.Where(b => b.Class == DetectedClass.Person).ToList();
        var inRoiIndexes = new List<int>();
        for (int i = 0; i < persons.Count; i++)
        {
            if (RoiEvaluator.IsCenterInRoi(persons[i], frameWidth, frameHeight, options))
                inRoiIndexes.Add(i);
        }

        if (inRoiIndexes.Count == 0) return FrameEvaluation.None;
        if (inRoiIndexes.Count >= 2) return FrameEvaluation.Multiple;

        var target = persons[inRoiIndexes[0]];
        if (!RoiEvaluator.MeetsMinHeight(target, frameHeight, options)) return FrameEvaluation.TooSmall;
        if (target.Height / frameHeight > options.MaxPersonHeightRatio
            || target.Width / Math.Max(1, target.Height) > options.MaxPersonWidthToHeightRatio)
            return FrameEvaluation.TooClose;

        var votes = EvaluateVotes(target, persons, boxes, options);
        return new FrameEvaluation(PersonRoiCondition.Qualified, votes);
    }

    // ROI에 정확히 1명(대상)이 있을 때 그 Person 박스를 반환한다. 대표 이미지의 Person confidence 기록 등에 사용.
    public static DetectedBox? FindSingleRoiPerson(IReadOnlyList<DetectedBox> boxes, int frameWidth, int frameHeight, SafetyVisionOptions options)
    {
        var inRoi = boxes.Where(b => b.Class == DetectedClass.Person && RoiEvaluator.IsCenterInRoi(b, frameWidth, frameHeight, options)).ToList();
        return inRoi.Count == 1 ? inRoi[0] : null;
    }

    private static IReadOnlyDictionary<EquipmentCode, FrameVote> EvaluateVotes(
        DetectedBox target,
        IReadOnlyList<DetectedBox> allPersons,
        IReadOnlyList<DetectedBox> allBoxes,
        SafetyVisionOptions options)
    {
        var result = new Dictionary<EquipmentCode, FrameVote>();
        foreach (var pair in EquipmentClassMap.All)
        {
            bool positive = HasUniqueConnectionToTarget(pair.Positive, target, allPersons, allBoxes, options);
            bool negative = HasUniqueConnectionToTarget(pair.Negative, target, allPersons, allBoxes, options);
            bool ambiguous = HasAmbiguousConnectionToTarget(pair.Positive, target, allPersons, allBoxes, options)
                || HasAmbiguousConnectionToTarget(pair.Negative, target, allPersons, allBoxes, options);
            result[pair.Code] = (positive, negative) switch
            {
                (true, true) => FrameVote.Conflict,
                (true, false) => FrameVote.Positive,
                (false, true) => FrameVote.Negative,
                _ when ambiguous => FrameVote.NoInfo,
                // 검사 가능한 크기의 전신 한 명이 확보된 상태에서는 착용/미착용 검출이 모두 없는
                // 장비를 미착용으로 본다. 현장 모델은 착용 클래스는 안정적이지만 미착용 클래스의
                // 신뢰도가 낮아, NoInfo를 유지하면 실제 미착용 전신 검사가 대부분 UNKNOWN이 된다.
                _ => FrameVote.Negative
            };
        }
        return result;
    }

    private static bool HasUniqueConnectionToTarget(
        DetectedClass ppeClass,
        DetectedBox target,
        IReadOnlyList<DetectedBox> allPersons,
        IReadOnlyList<DetectedBox> allBoxes,
        SafetyVisionOptions options)
    {
        foreach (var ppeBox in allBoxes)
        {
            if (ppeBox.Class != ppeClass) continue;

            int connectedCount = 0;
            bool connectedToTarget = false;
            foreach (var person in allPersons)
            {
                if (!PpeAssociationRules.IsCandidate(ppeClass, person, ppeBox, options)) continue;
                connectedCount++;
                if (person.Equals(target)) connectedToTarget = true;
                if (connectedCount > 1) break;
            }

            // 둘 이상의 Person에 연결되면 모호한 PPE로 제외(대상에게도 사용하지 않음).
            if (connectedCount == 1 && connectedToTarget) return true;
        }
        return false;
    }

    private static bool HasAmbiguousConnectionToTarget(
        DetectedClass ppeClass,
        DetectedBox target,
        IReadOnlyList<DetectedBox> allPersons,
        IReadOnlyList<DetectedBox> allBoxes,
        SafetyVisionOptions options)
    {
        foreach (var ppeBox in allBoxes.Where(b => b.Class == ppeClass))
        {
            bool connectedToTarget = PpeAssociationRules.IsCandidate(ppeClass, target, ppeBox, options);
            if (!connectedToTarget) continue;

            int connectedCount = allPersons.Count(person =>
                PpeAssociationRules.IsCandidate(ppeClass, person, ppeBox, options));
            if (connectedCount > 1) return true;
        }
        return false;
    }
}
