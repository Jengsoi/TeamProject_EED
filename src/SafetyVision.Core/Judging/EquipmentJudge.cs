using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Judging;

// 05_AI모델명세.md 7절: 조건 순서 고정. 단순 positive>negative 규칙을 사용하지 않는다.
// 임시 조치: 팀 결정에 따라 "미확인(Unknown)"을 내보내지 않고 전부 "미착용(NotWorn)"으로 표시한다.
// 05_AI모델명세.md의 "미검출을 미착용으로 확정하지 않는다" 원칙과는 반대 방향이므로, 되돌릴 때는
// 아래 각 Unknown 반환을 원래대로 되돌리면 된다.
public static class EquipmentJudge
{
    public static (EquipmentStatus Status, double? Score) Judge(int n, int p, int m, SafetyVisionOptions options)
    {
        int e = p + m;

        if (n < options.MinAnalysisFrames) return (EquipmentStatus.NotWorn, e > 0 ? (double)m / e : null);

        int minEvidence = Math.Max(options.MinEvidenceFrames, (int)Math.Ceiling(n * options.MinEvidenceRatio));
        if (e < minEvidence) return (EquipmentStatus.NotWorn, e > 0 ? (double)m / e : null);

        double positiveRatio = (double)p / e;
        if (positiveRatio >= options.DecisionRatio) return (EquipmentStatus.Worn, positiveRatio);

        double negativeRatio = (double)m / e;
        return (EquipmentStatus.NotWorn, negativeRatio);
    }

    public static EquipmentJudgement Judge(EquipmentCode code, EquipmentAccumulator accumulator, SafetyVisionOptions options)
    {
        var (status, score) = Judge(accumulator.Total, accumulator.Positive, accumulator.Negative, options);
        return new EquipmentJudgement(code, status, score, accumulator.Positive, accumulator.Negative, accumulator.Total);
    }
}
