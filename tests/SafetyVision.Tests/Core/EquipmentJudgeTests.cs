using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using SafetyVision.Core.Judging;
using Xunit;

namespace SafetyVision.Tests.Core;

public class EquipmentJudgeTests
{
    private static readonly SafetyVisionOptions Options = new();

    // 팀 결정 기본값(MinEvidenceRatio 0.25, DecisionRatio 0.50): 판별이 불가능할 때만 미확인.
    [Theory]
    [InlineData(12, 1, 0, EquipmentStatus.Unknown, null)]     // 유효표 3개 미만
    [InlineData(12, 7, 2, EquipmentStatus.Worn, 0.7777777777777778)]
    [InlineData(12, 2, 7, EquipmentStatus.NotWorn, 0.7777777777777778)]
    [InlineData(12, 5, 3, EquipmentStatus.Worn, 0.625)]       // 다수 쪽으로 판정
    [InlineData(12, 3, 5, EquipmentStatus.NotWorn, 0.625)]
    [InlineData(12, 4, 4, EquipmentStatus.Unknown, null)]     // 양쪽 근거가 같음
    [InlineData(12, 3, 0, EquipmentStatus.Worn, 1.0)]         // 유효표 3개면 판정
    [InlineData(12, 0, 3, EquipmentStatus.NotWorn, 1.0)]      // 유효표 3개면 미착용도 판정
    [InlineData(12, 2, 0, EquipmentStatus.Unknown, null)]     // 어느 쪽이든 유효표 3개 미만이면 판별 불가
    [InlineData(5, 3, 0, EquipmentStatus.Worn, 1.0)]
    [InlineData(4, 4, 0, EquipmentStatus.Unknown, null)]      // 분석 프레임 5개 미만
    public void Judge_DefaultOptions_UnknownOnlyWhenUndecidable(int n, int p, int m, EquipmentStatus expectedStatus, double? expectedScore)
    {
        AssertJudgement(EquipmentJudge.Judge(n, p, m, Options), expectedStatus, expectedScore);
    }

    // 05_AI모델명세.md 7절 예제: 명세 값(0.50/0.70)으로 설정하면 기존 규칙과 같다.
    [Theory]
    [InlineData(12, 1, 0, EquipmentStatus.Unknown, null)]     // 유효표 부족
    [InlineData(12, 7, 2, EquipmentStatus.Worn, 0.7777777777777778)]
    [InlineData(12, 2, 7, EquipmentStatus.NotWorn, 0.7777777777777778)]
    [InlineData(12, 4, 4, EquipmentStatus.Unknown, null)]     // 한쪽 비율 70% 미만
    [InlineData(12, 3, 0, EquipmentStatus.Unknown, null)]     // 최소 유효표 6 미달
    [InlineData(5, 3, 0, EquipmentStatus.Worn, 1.0)]
    [InlineData(4, 4, 0, EquipmentStatus.Unknown, null)]      // 분석 프레임 5개 미만
    public void Judge_SpecOptions_MatchesSpecExamples(int n, int p, int m, EquipmentStatus expectedStatus, double? expectedScore)
    {
        var specOptions = new SafetyVisionOptions { MinEvidenceRatio = 0.50, DecisionRatio = 0.70 };
        AssertJudgement(EquipmentJudge.Judge(n, p, m, specOptions), expectedStatus, expectedScore);
    }

    [Fact]
    public void Judge_UnknownScoreIsAlwaysNull()
    {
        var (status, score) = EquipmentJudge.Judge(0, 0, 0, Options);
        Assert.Equal(EquipmentStatus.Unknown, status);
        Assert.Null(score);
    }

    private static void AssertJudgement((EquipmentStatus Status, double? Score) actual, EquipmentStatus expectedStatus, double? expectedScore)
    {
        Assert.Equal(expectedStatus, actual.Status);
        if (expectedScore is null)
        {
            Assert.Null(actual.Score);
        }
        else
        {
            Assert.NotNull(actual.Score);
            Assert.Equal(expectedScore.Value, actual.Score!.Value, precision: 10);
        }
    }
}
