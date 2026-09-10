namespace SafetyVision.Core.Domain;

public sealed record FrameEvaluation(
    PersonRoiCondition Condition,
    IReadOnlyDictionary<EquipmentCode, FrameVote> Votes)
{
    public static readonly IReadOnlyDictionary<EquipmentCode, FrameVote> NoVotes =
        new Dictionary<EquipmentCode, FrameVote>();

    public static FrameEvaluation None { get; } = new(PersonRoiCondition.None, NoVotes);
    public static FrameEvaluation Multiple { get; } = new(PersonRoiCondition.Multiple, NoVotes);
    public static FrameEvaluation TooSmall { get; } = new(PersonRoiCondition.TooSmall, NoVotes);
}
