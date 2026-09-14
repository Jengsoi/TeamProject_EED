using SafetyVision.Core.Analysis;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using Xunit;

namespace SafetyVision.Tests.Core;

public class FrameAnalyzerTests
{
    private const int Width = 1280;
    private const int Height = 720;
    private static readonly SafetyVisionOptions Options = new();

    private static DetectedBox Person(float x, float y, float w, float h) =>
        new(DetectedClass.Person, x, y, w, h, 0.9f);

    [Fact]
    public void NoPersonInRoi_IsNone()
    {
        var boxes = new[] { Person(0, 0, 50, 50) }; // 완전히 ROI 밖
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, Options);
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
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, Options);
        Assert.Equal(PersonRoiCondition.Multiple, eval.Condition);
    }

    [Fact]
    public void PersonTooSmall_IsTooSmall()
    {
        var boxes = new[] { Person(560, 400, 100, 100) }; // height 100 < 720*0.4=288
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, Options);
        Assert.Equal(PersonRoiCondition.TooSmall, eval.Condition);
    }

    [Fact]
    public void CroppedClosePerson_IsTooClose()
    {
        var boxes = new[] { Person(250, 0, 780, 720) };
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, Options);
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
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, Options);
        Assert.Equal(PersonRoiCondition.Qualified, eval.Condition);
        Assert.Equal(FrameVote.Positive, eval.Votes[EquipmentCode.Hardhat]);
        Assert.Equal(FrameVote.Negative, eval.Votes[EquipmentCode.Vest]);
        Assert.Equal(FrameVote.Negative, eval.Votes[EquipmentCode.Mask]);
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
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, Options);
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
        var eval = FrameAnalyzer.Evaluate(boxes, Width, Height, Options);

        Assert.Equal(PersonRoiCondition.Qualified, eval.Condition);
        Assert.Equal(FrameVote.NoInfo, eval.Votes[EquipmentCode.Hardhat]);
    }
}
