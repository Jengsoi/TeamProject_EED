using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Judging;

// 05_AI모델명세.md 7절의 조건 순서를 따르되, 팀 결정으로 미확인은 판별이 불가능한 경우로만 좁힌다.
// 분석 프레임 또는 착용/미착용 근거가 최소 기준에 못 미치거나 양쪽 근거가 같을 때만 UNKNOWN이고,
// 그 밖에는 DecisionRatio(기본 0.50) 이상인 다수 쪽으로 착용/미착용을 정한다.
public static class EquipmentJudge
{
    public static (EquipmentStatus Status, double? Score) Judge(int n, int p, int m, SafetyVisionOptions options)
    {
        if (n < options.MinAnalysisFrames) return (EquipmentStatus.Unknown, null);

        int e = p + m;
        int minEvidence = Math.Max(options.MinEvidenceFrames, (int)Math.Ceiling(n * options.MinEvidenceRatio));
        if (e < minEvidence) return (EquipmentStatus.Unknown, null);

        double positiveRatio = (double)p / e;
        if (p > m && positiveRatio >= options.DecisionRatio) return (EquipmentStatus.Worn, positiveRatio);

        double negativeRatio = (double)m / e;
        if (m > p && negativeRatio >= options.DecisionRatio) return (EquipmentStatus.NotWorn, negativeRatio);

        return (EquipmentStatus.Unknown, null);
    }

    public static EquipmentJudgement Judge(EquipmentCode code, EquipmentAccumulator accumulator, SafetyVisionOptions options)
    {
        var (status, score) = Judge(accumulator.Total, accumulator.Positive, accumulator.Negative, options);
        return new EquipmentJudgement(code, status, score, accumulator.Positive, accumulator.Negative, accumulator.Total);
    }
}
