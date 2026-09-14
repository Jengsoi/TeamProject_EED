using SafetyVision.Core.Analysis;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using Xunit;

namespace SafetyVision.Tests.Core;

public class PpeConflictResolverTests
{
    private static SafetyVisionOptions NewOptions() => new SafetyVisionOptions();

    private static DetectedBox Person(float x = 500, float y = 200, float width = 200, float height = 400) =>
        new(DetectedClass.Person, x, y, width, height, 0.9f);

    private static DetectedBox Box(DetectedClass @class, float confidence, float x = 590, float y = 240) =>
        new(@class, x, y, 20, 20, confidence);

    private static (List<DetectedBox> Ppe, List<DetectedBox> Result) Resolve(
        IReadOnlyList<DetectedBox> persons,
        params DetectedBox[] boxes)
    {
        var ppeBoxes = boxes.ToList();
        var resultBoxes = boxes.ToList();
        PpeConflictResolver.Resolve(persons, ppeBoxes, resultBoxes, NewOptions());
        return (ppeBoxes, resultBoxes);
    }

    [Fact]
    public void WeakHardhatAndStrongNoHardhat_KeepNoHardhat()
    {
        var person = Person();
        var hardhat = Box(DetectedClass.Hardhat, 0.01f);
        var noHardhat = Box(DetectedClass.NoHardhat, 0.30f);

        var (ppe, result) = Resolve(new[] { person }, hardhat, noHardhat);

        Assert.DoesNotContain(hardhat, ppe);
        Assert.DoesNotContain(hardhat, result);
        Assert.Contains(noHardhat, ppe);
        Assert.Contains(noHardhat, result);
    }

    [Fact]
    public void StrongHardhatAndWeakNoHardhat_KeepHardhat()
    {
        var person = Person();
        var hardhat = Box(DetectedClass.Hardhat, 0.80f);
        var noHardhat = Box(DetectedClass.NoHardhat, 0.06f);

        var (ppe, result) = Resolve(new[] { person }, hardhat, noHardhat);

        Assert.Contains(hardhat, ppe);
        Assert.Contains(hardhat, result);
        Assert.DoesNotContain(noHardhat, ppe);
        Assert.DoesNotContain(noHardhat, result);
    }

    [Fact]
    public void EqualHeadwearConfidence_KeepBoth()
    {
        var person = Person();
        var hardhat = Box(DetectedClass.Hardhat, 0.30f);
        var noHardhat = Box(DetectedClass.NoHardhat, 0.30f);

        var (ppe, result) = Resolve(new[] { person }, hardhat, noHardhat);

        Assert.Contains(hardhat, ppe);
        Assert.Contains(noHardhat, ppe);
        Assert.Contains(hardhat, result);
        Assert.Contains(noHardhat, result);
    }

    [Fact]
    public void MaskInsideHeadwearBoundary_IsRemovedButMaskBelowBoundaryRemains()
    {
        var person = Person();
        var hardhat = Box(DetectedClass.Hardhat, 0.80f, y: 240);
        var inside = Box(DetectedClass.Mask, 0.90f, y: 240);
        var below = Box(DetectedClass.NoMask, 0.80f, y: 300);

        var (ppe, result) = Resolve(new[] { person }, hardhat, inside, below);

        Assert.DoesNotContain(inside, ppe);
        Assert.DoesNotContain(inside, result);
        Assert.Contains(below, ppe);
        Assert.Contains(below, result);
    }

    [Fact]
    public void MultipleMaskCandidates_KeepOnlyHighestConfidence()
    {
        var person = Person();
        var lowMask = Box(DetectedClass.Mask, 0.40f, y: 240);
        var winner = Box(DetectedClass.NoMask, 0.80f, y: 260);
        var middleMask = Box(DetectedClass.Mask, 0.60f, y: 280);

        var (ppe, result) = Resolve(new[] { person }, lowMask, winner, middleMask);

        Assert.Equal(new[] { winner }, ppe);
        Assert.Equal(new[] { winner }, result);
    }

    [Fact]
    public void NoHardhatConnectedOnlyToAnotherPerson_IsNotRemoved()
    {
        var target = Person();
        var other = Person(x: 50);
        var targetHardhat = Box(DetectedClass.Hardhat, 0.80f);
        var targetNoHardhat = Box(DetectedClass.NoHardhat, 0.06f);
        var otherNoHardhat = Box(DetectedClass.NoHardhat, 0.90f, x: 140);

        var (ppe, result) = Resolve(
            new[] { target, other },
            target,
            other,
            targetHardhat,
            targetNoHardhat,
            otherNoHardhat);

        Assert.Contains(target, ppe);
        Assert.Contains(other, ppe);
        Assert.DoesNotContain(targetNoHardhat, ppe);
        Assert.DoesNotContain(targetNoHardhat, result);
        Assert.Contains(otherNoHardhat, ppe);
        Assert.Contains(otherNoHardhat, result);
    }
}
