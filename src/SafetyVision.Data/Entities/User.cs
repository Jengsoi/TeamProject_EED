namespace SafetyVision.Data.Entities;

public sealed class User
{
    public long Id { get; set; }
    public string LoginId { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
