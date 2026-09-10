namespace SafetyVision.Data.Entities;

public sealed class InspectionItem
{
    public long Id { get; set; }
    public long InspectionId { get; set; }
    public string EquipmentCode { get; set; } = ""; // hardhat / vest / mask
    public string Status { get; set; } = "";          // WORN / NOT_WORN / UNKNOWN
    public double? Score { get; set; }
    public int PositiveFrames { get; set; }
    public int NegativeFrames { get; set; }
    public int TotalFrames { get; set; }
    public DateTime CreatedAt { get; set; }

    public Inspection Inspection { get; set; } = null!;
}
