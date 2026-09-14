using SafetyVision.Core.Configuration;
using Xunit;

namespace SafetyVision.Tests.Core;

public class SafetyVisionOptionsTests
{
    [Fact]
    public void ConnectionStringWithoutPassword_IsRejected()
    {
        var options = ValidOptions();
        options.ConnectionStrings.MySql = "Server=localhost;Database=safetyvision;User=safetyvision_app;";

        var errors = options.Validate().ToArray();

        Assert.Contains(errors, error => error.Contains("ConnectionStrings__MySql", StringComparison.Ordinal));
    }

    [Fact]
    public void ConnectionStringWithPassword_IsAccepted()
    {
        var options = ValidOptions();

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void InvalidListenAddress_IsRejected()
    {
        var options = ValidOptions();
        options.ListenAddress = "not-an-ip-address";

        var errors = options.Validate().ToArray();

        Assert.Contains(errors, error => error.Contains("ListenAddress", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultListenAddress_IsLoopback()
    {
        Assert.Equal("127.0.0.1", new SafetyVisionOptions().ListenAddress);
    }

    [Fact]
    public void DefaultPpeThresholds_KeepTheCalibratedValues()
    {
        var options = new SafetyVisionOptions();

        Assert.Equal(0.005, options.NoWearDetectionConfidence, precision: 6);
        Assert.Equal(0.005, options.HardhatDetectionConfidence, precision: 6);
        Assert.Equal(0.00001, options.MaskDetectionConfidence, precision: 8);
        Assert.Equal(0.25, options.MinEvidenceRatio, precision: 6);
        Assert.Equal(0.50, options.DecisionRatio, precision: 6);
    }

    private static SafetyVisionOptions ValidOptions() => new()
    {
        ConnectionStrings = new ConnectionStringsOptions
        {
            MySql = "Server=localhost;Database=safetyvision;User=safetyvision_app;Password=test-password;",
        },
    };
}
