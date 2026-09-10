using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Judging;

public static class RepresentativeFrameSelector
{
    // 04_DB설계.md 6절: 한쪽(착용/미착용)만 검출된 장비 종류가 가장 많은 프레임. 동률은 더 나중 프레임.
    public static int SelectIndex(IReadOnlyList<IReadOnlyDictionary<EquipmentCode, FrameVote>> frameVotes)
    {
        int bestIndex = -1;
        int bestCount = -1;
        for (int i = 0; i < frameVotes.Count; i++)
        {
            int oneSidedCount = frameVotes[i].Values.Count(v => v is FrameVote.Positive or FrameVote.Negative);
            if (oneSidedCount >= bestCount)
            {
                bestCount = oneSidedCount;
                bestIndex = i;
            }
        }
        return bestIndex;
    }
}
