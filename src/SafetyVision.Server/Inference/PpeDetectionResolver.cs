using SafetyVision.Core.Analysis;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Server.Inference;

internal static class PpeDetectionResolver
{
    public static List<DetectedBox> Resolve(IReadOnlyList<DetectedBox> persons,
        List<DetectedBox> ppeBoxes, SafetyVisionOptions options)
    {
        var result = ppeBoxes.Where(b => b.Class != DetectedClass.Person).ToList();
        ResolveHeadwearConflicts(persons, ppeBoxes, result, options);
        ResolveMaskConflicts(persons, ppeBoxes, result, options);
        result.RemoveAll(box => !persons.Any(person =>
            PpeAssociationRules.IsCandidate(box.Class, person, box, options)));
        result.AddRange(persons);
        return result;
    }

    private static void ResolveHeadwearConflicts(IReadOnlyList<DetectedBox> persons,
        List<DetectedBox> ppeBoxes, List<DetectedBox> result, SafetyVisionOptions options)
    {
        foreach (var person in persons)
        {
            bool hasHardhat = ppeBoxes.Any(b => b.Class == DetectedClass.Hardhat
                && PpeAssociationRules.IsCandidate(DetectedClass.Hardhat, person, b, options));
            if (!hasHardhat) continue;

            ppeBoxes.RemoveAll(b => b.Class == DetectedClass.NoHardhat
                && PpeAssociationRules.IsCandidate(DetectedClass.NoHardhat, person, b, options));
            result.RemoveAll(b => b.Class == DetectedClass.NoHardhat
                && PpeAssociationRules.IsCandidate(DetectedClass.NoHardhat, person, b, options));
        }
    }

    private static void ResolveMaskConflicts(IReadOnlyList<DetectedBox> persons,
        List<DetectedBox> ppeBoxes, List<DetectedBox> result, SafetyVisionOptions options)
    {
        foreach (var person in persons)
        {
            var headwear = ppeBoxes
                .Where(b => b.Class is DetectedClass.Hardhat or DetectedClass.NoHardhat)
                .Where(b => PpeAssociationRules.IsCandidate(b.Class, person, b, options))
                .OrderByDescending(b => b.Confidence)
                .Select(b => (DetectedBox?)b)
                .FirstOrDefault();

            if (headwear is { } head)
            {
                double faceBoundary = head.Y + head.Height * 1.15;
                RemoveMaskCandidates(ppeBoxes, person, options, b => b.CenterY <= faceBoundary);
                RemoveMaskCandidates(result, person, options, b => b.CenterY <= faceBoundary);
            }

            var candidates = ppeBoxes.Where(IsMaskClass)
                .Where(b => PpeAssociationRules.IsCandidate(b.Class, person, b, options))
                .OrderByDescending(b => b.Confidence)
                .ToList();
            if (candidates.Count <= 1) continue;

            var winner = candidates[0];
            RemoveMaskCandidates(ppeBoxes, person, options, b => !b.Equals(winner));
            RemoveMaskCandidates(result, person, options, b => !b.Equals(winner));
        }
    }

    private static void RemoveMaskCandidates(List<DetectedBox> boxes, DetectedBox person,
        SafetyVisionOptions options, Func<DetectedBox, bool> predicate) =>
        boxes.RemoveAll(b => IsMaskClass(b) && predicate(b)
            && PpeAssociationRules.IsCandidate(b.Class, person, b, options));

    private static bool IsMaskClass(DetectedBox box) =>
        box.Class is DetectedClass.Mask or DetectedClass.NoMask;
}
