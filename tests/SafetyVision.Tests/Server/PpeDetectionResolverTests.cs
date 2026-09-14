using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using SafetyVision.Server.Inference;

namespace SafetyVision.Tests.Server;

public class PpeDetectionResolverTests
{
    private static readonly SafetyVisionOptions Options = new();
    private static readonly DetectedBox Person = new(DetectedClass.Person, 500, 50, 250, 650, .9f);

    [Fact]
    public void Resolve_RemovesMaskCandidateInsideHardhat()
    {
        var ppe = new List<DetectedBox>
        {
            new(DetectedClass.Hardhat, 570, 60, 110, 80, .8f),
            new(DetectedClass.Mask, 590, 75, 70, 50, .7f),
            new(DetectedClass.NoMask, 590, 155, 70, 50, .6f),
        };
        var result = PpeDetectionResolver.Resolve([Person], ppe, Options);
        Assert.DoesNotContain(result, b => b.Class == DetectedClass.Mask);
        Assert.Contains(result, b => b.Class == DetectedClass.NoMask);
    }

    [Fact]
    public void Resolve_KeepsOnlyHighestMaskDecisionForPerson()
    {
        var ppe = new List<DetectedBox>
        {
            new(DetectedClass.Mask, 590, 150, 70, 50, .8f),
            new(DetectedClass.NoMask, 590, 155, 70, 50, .3f),
        };
        var result = PpeDetectionResolver.Resolve([Person], ppe, Options);
        Assert.Single(result, b => b.Class is DetectedClass.Mask or DetectedClass.NoMask);
        Assert.Contains(result, b => b.Class == DetectedClass.Mask);
    }
}
