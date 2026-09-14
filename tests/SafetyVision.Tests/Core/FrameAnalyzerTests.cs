using SafetyVision.Core.Analysis;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using Xunit;

namespace SafetyVision.Tests.Core;

public class FrameAnalyzerTests
{
    private const int Width = 1280;
    private const int Height = 720;

    // 크기·비율 경계 테스트가 옵션 기본값 변경에 흔들리지 않도록 값을 명시한다.
    private static SafetyVisionOptions SizeOptions(double minHeightRatio = 0.40, double maxWidthToHeightRatio = 0.75) => new()
    {
        MinPersonHeightRatio = minHeightRatio,
        MaxPersonHeightRatio = 0.99,
        MaxPersonWidthToHeightRatio = maxWidthToHeightRatio,
    };

    private static DetectedBox Person(float x, float y, float w, float h) =>
        new(DetectedClass.Person, x, y, w, h, 0.9f);

    [Fact]
    public void NoPersonInRoi_IsNone()
    {
        var boxes = new[] { Person(0, 0, 50, 50) }; // 완전히 ROI 밖
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, new SafetyVisionOptions());
        Assert.Equal(PersonRoiCondition.None, eval.Condition);
    }

    [Fact]
    public void TwoPersonsInRoi_IsMultiple()
    {
        var boxes = new[]
        {
            Person(500, 200, 200, 400),
            Person(700, 200, 200, 400),
        };
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, new SafetyVisionOptions());
        Assert.Equal(PersonRoiCondition.Multiple, eval.Condition);
    }

    [Fact]
    public void PersonTooSmall_IsTooSmall()
    {
        var boxes = new[] { Person(560, 400, 100, 100) }; // height 100 < 720*0.4=288
        var options = SizeOptions();
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, options);
        Assert.Equal(PersonRoiCondition.TooSmall, eval.Condition);
    }

    [Fact]
    public void CroppedClosePerson_IsTooClose()
    {
        var boxes = new[] { Person(250, 0, 780, 720) };
        var options = SizeOptions();
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, options);
        Assert.Equal(PersonRoiCondition.TooClose, eval.Condition);
    }

    [Fact]
    public void QualifiedPersonWithConnectedHardhat_VotesPositive()
    {
        var boxes = new DetectedBox[]
        {
            Person(500, 200, 200, 400),
            new(DetectedClass.Hardhat, 590, 240, 20, 20, 0.8f), // center (600,250) inside head region
        };
        var options = SizeOptions();
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, options);
        Assert.Equal(PersonRoiCondition.Qualified, eval.Condition);
        Assert.Equal(FrameVote.Positive, eval.Votes[EquipmentCode.Hardhat]);
        Assert.Equal(FrameVote.NoInfo, eval.Votes[EquipmentCode.Vest]);
        Assert.Equal(FrameVote.NoInfo, eval.Votes[EquipmentCode.Mask]);
    }

    [Fact]
    public void HardhatBoth_VotesConflict()
    {
        var boxes = new DetectedBox[]
        {
            Person(500, 200, 200, 400),
            new(DetectedClass.Hardhat, 590, 240, 20, 20, 0.8f),
            new(DetectedClass.NoHardhat, 600, 250, 20, 20, 0.7f),
        };
        var options = SizeOptions();
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, options);
        Assert.Equal(FrameVote.Conflict, eval.Votes[EquipmentCode.Hardhat]);
    }

    [Fact]
    public void PpeConnectedToTwoPersons_IsAmbiguousAndExcluded()
    {
        // target A는 ROI 안, D는 ROI 밖(중심점 기준)이지만 두 사람의 머리 후보 영역이 겹친다.
        var target = Person(500, 200, 200, 400);
        var otherOutsideRoi = Person(600, 200, 900, 400); // center x = 1050 > RoiRight(1024)
        var hardhat = new DetectedBox(DetectedClass.Hardhat, 590, 240, 20, 20, 0.8f); // center (600,250)

        var boxes = new[] { target, otherOutsideRoi, hardhat };
        var options = SizeOptions();
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, options);

        Assert.Equal(PersonRoiCondition.Qualified, eval.Condition);
        Assert.Equal(FrameVote.NoInfo, eval.Votes[EquipmentCode.Hardhat]);
    }

    [Fact]
    public void QualifiedPersonWithoutConnectedPpe_VotesNoInfoForAllEquipment()
    {
        var boxes = new[] { Person(500, 200, 200, 400) };
        var options = SizeOptions(minHeightRatio: 0.20, maxWidthToHeightRatio: 1.5);

        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, options);

        Assert.Equal(PersonRoiCondition.Qualified, eval.Condition);
        Assert.Equal(FrameVote.NoInfo, eval.Votes[EquipmentCode.Hardhat]);
        Assert.Equal(FrameVote.NoInfo, eval.Votes[EquipmentCode.Vest]);
        Assert.Equal(FrameVote.NoInfo, eval.Votes[EquipmentCode.Mask]);
    }

    [Fact]
    public void UpperBodyPerson_IsQualifiedAtWideRatioAndTooCloseAtNarrowRatio()
    {
        var boxes = new[] { Person(390, 160, 500, 400) };
        var qualified = FrameAnalyzer.Evaluate(boxes, Width, Height, SizeOptions(minHeightRatio: 0.20, maxWidthToHeightRatio: 1.5));
        var tooClose = FrameAnalyzer.Evaluate(boxes, Width, Height, SizeOptions(minHeightRatio: 0.20, maxWidthToHeightRatio: 0.75));

        Assert.Equal(PersonRoiCondition.Qualified, qualified.Condition);
        Assert.Equal(PersonRoiCondition.TooClose, tooClose.Condition);
    }
}
