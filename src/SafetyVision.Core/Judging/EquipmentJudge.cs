using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Judging;

// 05_AI모델명세.md 7절: 조건 순서 고정. 단순 positive>negative 규칙을 사용하지 않는다.
// 개별 장비 판정은 착용/미착용/미확인 3가지를 유지한다(스펙 원안). 최종 결과만 2가지로 단순화하는 것은
// InspectionOutcomeCalculator에서 처리한다.
public static class EquipmentJudge
{
    public static (EquipmentStatus Status, double? Score) Judge(int n, int p, int m, SafetyVisionOptions options)
    {
        if (n < options.MinAnalysisFrames) return (EquipmentStatus.Unknown, null);

        int e = p + m;
        int minEvidence = Math.Max(options.MinEvidenceFrames, (int)Math.Ceiling(n * options.MinEvidenceRatio));
        if (e < minEvidence) return (EquipmentStatus.Unknown, null);

        double positiveRatio = (double)p / e;
        if (positiveRatio >= options.DecisionRatio) return (EquipmentStatus.Worn, positiveRatio);

        double negativeRatio = (double)m / e;
        if (negativeRatio >= options.DecisionRatio) return (EquipmentStatus.NotWorn, negativeRatio);

        return (EquipmentStatus.Unknown, null);
    }

    public static EquipmentJudgement Judge(EquipmentCode code, EquipmentAccumulator accumulator, SafetyVisionOptions options)
    {
        var (status, score) = Judge(accumulator.Total, accumulator.Positive, accumulator.Negative, options);
        return new EquipmentJudgement(code, status, score, accumulator.Positive, accumulator.Negative, accumulator.Total);
    }
}
