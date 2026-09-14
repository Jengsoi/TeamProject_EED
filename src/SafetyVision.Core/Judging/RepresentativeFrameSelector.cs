using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Judging;

public static class RepresentativeFrameSelector
{
    // 04_DB설계.md 6절: 한쪽(착용/미착용)만 검출된 장비 종류가 가장 많은 프레임을 우선한다.
    // 단, 검사 초반(자세를 잡기 전) 프레임이 우연히 항목을 더 많이 잡았다는 이유만으로
    // 이후 자세를 바로잡은 최근 프레임을 밀어내지 않도록, 최댓값에서 크게 뒤처지지 않는
    // 프레임들 중에서는 가장 나중(최근) 프레임을 대표 프레임으로 선택한다.
    private const int NearMaxTolerance = 1;

    public static int SelectIndex(IReadOnlyList<IReadOnlyDictionary<EquipmentCode, FrameVote>> frameVotes)
    {
        if (frameVotes.Count == 0)
            return -1;

        var oneSidedCounts = frameVotes
            .Select(votes => votes.Values.Count(v => v is FrameVote.Positive or FrameVote.Negative))
            .ToList();
        int maxCount = oneSidedCounts.Max();
        int threshold = Math.Max(0, maxCount - NearMaxTolerance);

        int bestIndex = -1;
        for (int i = 0; i < oneSidedCounts.Count; i++)
        {
            if (oneSidedCounts[i] >= threshold)
                bestIndex = i; // 계속 갱신되므로 조건을 만족하는 가장 나중 프레임이 남는다.
        }
        return bestIndex;
    }
}
