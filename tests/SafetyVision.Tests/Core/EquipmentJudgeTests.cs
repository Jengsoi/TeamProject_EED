using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using SafetyVision.Core.Judging;
using Xunit;

namespace SafetyVision.Tests.Core;

// 05_AI모델명세.md 7절 예제 표를 그대로 검증한다.
public class EquipmentJudgeTests
{
    private static readonly SafetyVisionOptions Options = new();

    [Theory]
    [InlineData(12, 1, 0, EquipmentStatus.Unknown, null)]     // 유효표 부족
    [InlineData(12, 7, 2, EquipmentStatus.Worn, 0.7777777777777778)]
    [InlineData(12, 2, 7, EquipmentStatus.NotWorn, 0.7777777777777778)]
    [InlineData(12, 4, 4, EquipmentStatus.Unknown, null)]     // 한쪽 비율 70% 미만
    [InlineData(12, 3, 0, EquipmentStatus.Unknown, null)]     // 최소 유효표 6 미달
    [InlineData(5, 3, 0, EquipmentStatus.Worn, 1.0)]
    [InlineData(4, 4, 0, EquipmentStatus.Unknown, null)]      // 분석 프레임 5개 미만
    public void Judge_MatchesSpecExamples(int n, int p, int m, EquipmentStatus expectedStatus, double? expectedScore)
    {
        var (status, score) = EquipmentJudge.Judge(n, p, m, Options);

        Assert.Equal(expectedStatus, status);
        if (expectedScore is null)
        {
            Assert.Null(score);
        }
        else
        {
            Assert.NotNull(score);
            Assert.Equal(expectedScore.Value, score!.Value, precision: 10);
        }
    }

    [Fact]
    public void Judge_UnknownScoreIsAlwaysNull()
    {
        var (status, score) = EquipmentJudge.Judge(0, 0, 0, Options);
        Assert.Equal(EquipmentStatus.Unknown, status);
        Assert.Null(score);
    }
}
