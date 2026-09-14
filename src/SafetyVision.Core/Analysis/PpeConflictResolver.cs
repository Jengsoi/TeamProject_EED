using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Analysis;

// 외부 상태에 의존하지 않고 사람별 PPE 후보 목록만 갱신한다.
public static class PpeConflictResolver
{
    public static void Resolve(
        IReadOnlyList<DetectedBox> persons,
        List<DetectedBox> ppeBoxes,
        List<DetectedBox> resultBoxes,
        SafetyVisionOptions options)
    {
        foreach (var person in persons)
        {
            var hardhatCandidates = ppeBoxes
                .Where(box => box.Class == DetectedClass.Hardhat
                    && PpeAssociationRules.IsCandidate(DetectedClass.Hardhat, person, box, options))
                .ToList();
            var noHardhatCandidates = ppeBoxes
                .Where(box => box.Class == DetectedClass.NoHardhat
                    && PpeAssociationRules.IsCandidate(DetectedClass.NoHardhat, person, box, options))
                .ToList();

            if (hardhatCandidates.Count > 0 && noHardhatCandidates.Count > 0)
            {
                float hardhatConfidence = hardhatCandidates.Max(box => box.Confidence);
                float noHardhatConfidence = noHardhatCandidates.Max(box => box.Confidence);
                if (hardhatConfidence < noHardhatConfidence)
                {
                    RemoveConnected(ppeBoxes, person, options,
                        box => box.Class == DetectedClass.Hardhat);
                    RemoveConnected(resultBoxes, person, options,
                        box => box.Class == DetectedClass.Hardhat);
                }
                else if (hardhatConfidence > noHardhatConfidence)
                {
                    RemoveConnected(ppeBoxes, person, options,
                        box => box.Class == DetectedClass.NoHardhat);
                    RemoveConnected(resultBoxes, person, options,
                        box => box.Class == DetectedClass.NoHardhat);
                }
            }
        }

        foreach (var person in persons)
        {
            var headwear = ppeBoxes
                .Where(box => IsHeadwear(box.Class)
                    && PpeAssociationRules.IsCandidate(box.Class, person, box, options))
                .OrderByDescending(box => box.Confidence)
                .Select(box => (DetectedBox?)box)
                .FirstOrDefault();

            if (headwear is { } head)
            {
                // 헤드웨어 내부의 낮은 Mask 후보가 얼굴 후보를 가리지 않도록 먼저 제거한다.
                double faceBoundary = head.Y + head.Height * 1.15;
                RemoveConnected(ppeBoxes, person, options,
                    box => IsMask(box.Class) && box.CenterY <= faceBoundary);
                RemoveConnected(resultBoxes, person, options,
                    box => IsMask(box.Class) && box.CenterY <= faceBoundary);
            }

            var maskCandidates = ppeBoxes
                .Where(box => IsMask(box.Class)
                    && PpeAssociationRules.IsCandidate(box.Class, person, box, options))
                .OrderByDescending(box => box.Confidence)
                .ToList();
            if (maskCandidates.Count <= 1)
                continue;

            var winner = maskCandidates[0];
            RemoveConnected(ppeBoxes, person, options,
                box => IsMask(box.Class) && !box.Equals(winner));
            RemoveConnected(resultBoxes, person, options,
                box => IsMask(box.Class) && !box.Equals(winner));
        }
    }

    private static bool IsHeadwear(DetectedClass ppeClass) =>
        ppeClass is DetectedClass.Hardhat or DetectedClass.NoHardhat;

    private static bool IsMask(DetectedClass ppeClass) =>
        ppeClass is DetectedClass.Mask or DetectedClass.NoMask;

    private static void RemoveConnected(
        List<DetectedBox> boxes,
        DetectedBox person,
        SafetyVisionOptions options,
        Func<DetectedBox, bool> condition)
    {
        boxes.RemoveAll(box => condition(box)
            && PpeAssociationRules.IsCandidate(box.Class, person, box, options));
    }
}
