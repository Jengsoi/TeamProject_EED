using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Analysis;

public sealed record EquipmentClassPair(EquipmentCode Code, DetectedClass Positive, DetectedClass Negative);

public static class EquipmentClassMap
{
    public static readonly IReadOnlyList<EquipmentClassPair> All = new[]
    {
        new EquipmentClassPair(EquipmentCode.Hardhat, DetectedClass.Hardhat, DetectedClass.NoHardhat),
        new EquipmentClassPair(EquipmentCode.Vest, DetectedClass.SafetyVest, DetectedClass.NoSafetyVest),
        new EquipmentClassPair(EquipmentCode.Mask, DetectedClass.Mask, DetectedClass.NoMask),
    };

    // DB EquipmentCode 컬럼 값(04_DB설계.md): hardhat / vest / mask
    public static string ToDbCode(EquipmentCode code) => code switch
    {
        EquipmentCode.Hardhat => "hardhat",
        EquipmentCode.Vest => "vest",
        EquipmentCode.Mask => "mask",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    public static EquipmentCode FromDbCode(string code) => code switch
    {
        "hardhat" => EquipmentCode.Hardhat,
        "vest" => EquipmentCode.Vest,
        "mask" => EquipmentCode.Mask,
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };
}
