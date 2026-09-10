namespace SafetyVision.Core.Domain;

public sealed class EquipmentAccumulator
{
    public int Positive { get; private set; }
    public int Negative { get; private set; }
    public int Total { get; private set; }

    public void Apply(FrameVote vote)
    {
        Total++;
        switch (vote)
        {
            case FrameVote.Positive:
                Positive++;
                break;
            case FrameVote.Negative:
                Negative++;
                break;
        }
    }

    public void Reset()
    {
        Positive = 0;
        Negative = 0;
        Total = 0;
    }
}
