using SafetyVision.Core.Domain;
using SafetyVision.Core.Judging;
using Xunit;

namespace SafetyVision.Tests.Core;

public class InspectionOutcomeCalculatorTests
{
    private static EquipmentJudgement Item(EquipmentCode code, EquipmentStatus status) =>
        new(code, status, status == EquipmentStatus.Unknown ? null : 0.8, 0, 0, 12);

    [Fact]
    public void AllWorn_IsNormal()
    {
        var items = new[]
        {
            Item(EquipmentCode.Hardhat, EquipmentStatus.Worn),
            Item(EquipmentCode.Vest, EquipmentStatus.Worn),
            Item(EquipmentCode.Mask, EquipmentStatus.Worn),
        };
        Assert.Equal(InspectionResultType.Normal, InspectionOutcomeCalculator.Combine(items));
    }

    [Fact]
    public void AnyNotWorn_IsCheckRequired_EvenWithUnknowns()
    {
        var items = new[]
        {
            Item(EquipmentCode.Hardhat, EquipmentStatus.NotWorn),
            Item(EquipmentCode.Vest, EquipmentStatus.Unknown),
            Item(EquipmentCode.Mask, EquipmentStatus.Worn),
        };
        Assert.Equal(InspectionResultType.CheckRequired, InspectionOutcomeCalculator.Combine(items));
    }

    [Fact]
    public void UnknownWithoutNotWorn_IsCheckRequired()
    {
        // 임시 조치(팀 결정): 최종 결과는 정상/점검 필요 2가지로만 단순화. 미확인 장비가 있어도 점검 필요로 취급.
        var items = new[]
        {
            Item(EquipmentCode.Hardhat, EquipmentStatus.Worn),
            Item(EquipmentCode.Vest, EquipmentStatus.Unknown),
            Item(EquipmentCode.Mask, EquipmentStatus.Worn),
        };
        Assert.Equal(InspectionResultType.CheckRequired, InspectionOutcomeCalculator.Combine(items));
    }
}
