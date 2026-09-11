using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using SafetyVision.Core.Judging;
using Xunit;

namespace SafetyVision.Tests.Core;

// 임시 조치: 팀 결정에 따라 Unknown을 내보내지 않고 전부 NotWorn으로 표시하도록 변경했다.
// 05_AI모델명세.md 7절의 원래 예제 표(Unknown 기대값)와는 의도적으로 다르다.
public class EquipmentJudgeTests
{
    private static readonly SafetyVisionOptions Options = new();

    [Theory]
    [InlineData(12, 1, 0, EquipmentStatus.NotWorn, 0.0)]        // 유효표 부족 -> 미착용
    [InlineData(12, 7, 2, EquipmentStatus.Worn, 0.7777777777777778)]
    [InlineData(12, 2, 7, EquipmentStatus.NotWorn, 0.7777777777777778)]
    [InlineData(12, 4, 4, EquipmentStatus.NotWorn, 0.5)]        // 한쪽 비율 70% 미만 -> 미착용
    [InlineData(12, 3, 0, EquipmentStatus.NotWorn, 0.0)]        // 최소 유효표 6 미달 -> 미착용
    [InlineData(5, 3, 0, EquipmentStatus.Worn, 1.0)]
    [InlineData(4, 4, 0, EquipmentStatus.NotWorn, 0.0)]         // 분석 프레임 5개 미만 -> 미착용
    public void Judge_UnknownReplacedByNotWorn(int n, int p, int m, EquipmentStatus expectedStatus, double? expectedScore)
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
    public void Judge_NoEvidenceAtAll_ReturnsNotWornWithNullScore()
    {
        var (status, score) = EquipmentJudge.Judge(0, 0, 0, Options);
        Assert.Equal(EquipmentStatus.NotWorn, status);
        Assert.Null(score);
    }
}
