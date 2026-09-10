namespace SafetyVision.Core.Domain;

// 04_DB설계.md 컬럼 값 문자열과 Core enum 간 매핑.
public static class DbCodes
{
    public static string ToDbCode(this EquipmentStatus status) => status switch
    {
        EquipmentStatus.Worn => "WORN",
        EquipmentStatus.NotWorn => "NOT_WORN",
        EquipmentStatus.Unknown => "UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static EquipmentStatus EquipmentStatusFromDbCode(string code) => code switch
    {
        "WORN" => EquipmentStatus.Worn,
        "NOT_WORN" => EquipmentStatus.NotWorn,
        "UNKNOWN" => EquipmentStatus.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    public static string ToDbCode(this InspectionResultType result) => result switch
    {
        InspectionResultType.Normal => "NORMAL",
        InspectionResultType.CheckRequired => "CHECK_REQUIRED",
        InspectionResultType.Unconfirmed => "UNCONFIRMED",
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    public static InspectionResultType InspectionResultFromDbCode(string code) => code switch
    {
        "NORMAL" => InspectionResultType.Normal,
        "CHECK_REQUIRED" => InspectionResultType.CheckRequired,
        "UNCONFIRMED" => InspectionResultType.Unconfirmed,
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };
}
