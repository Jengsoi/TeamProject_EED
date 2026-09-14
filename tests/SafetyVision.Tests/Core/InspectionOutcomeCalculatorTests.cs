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
    public void UnknownWithoutNotWorn_IsUnconfirmed()
    {
        var items = new[]
        {
            Item(EquipmentCode.Hardhat, EquipmentStatus.Worn),
            Item(EquipmentCode.Vest, EquipmentStatus.Unknown),
            Item(EquipmentCode.Mask, EquipmentStatus.Worn),
        };
        Assert.Equal(InspectionResultType.Unconfirmed, InspectionOutcomeCalculator.Combine(items));
    }
}
