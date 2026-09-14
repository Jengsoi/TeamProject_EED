namespace SafetyVision.Data.Entities;

public sealed class Inspection
{
    public long Id { get; set; }
    public Guid InspectionKey { get; set; }
    public DateTime InspectedAt { get; set; }
    public string Result { get; set; } = ""; // NORMAL / CHECK_REQUIRED / UNCONFIRMED
    public double? PersonConfidence { get; set; }
    public string ImagePath { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public List<InspectionItem> Items { get; set; } = [];
}
