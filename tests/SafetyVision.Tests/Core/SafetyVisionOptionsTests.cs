using SafetyVision.Core.Configuration;
using Xunit;

namespace SafetyVision.Tests.Core;

public sealed class SafetyVisionOptionsTests
{
    [Fact]
    public void Validate_RejectsMissingDatabasePassword()
    {
        var options = new SafetyVisionOptions
        {
            ConnectionStrings = new ConnectionStringsOptions
            {
                MySql = "Server=localhost;Database=safetyvision;User=safetyvision_app;"
            }
        };

        Assert.Contains(options.Validate(), message => message.Contains("Password", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsEnvironmentProvidedDatabasePassword()
    {
        var options = new SafetyVisionOptions
        {
            ConnectionStrings = new ConnectionStringsOptions
            {
                MySql = "Server=localhost;Database=safetyvision;User=safetyvision_app;Password=test;"
            }
        };

        Assert.DoesNotContain(options.Validate(), message => message.Contains("Password", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsInvalidListenAddress()
    {
        var options = new SafetyVisionOptions
        {
            ListenAddress = "not-an-ip",
            ConnectionStrings = new ConnectionStringsOptions
            {
                MySql = "Server=localhost;Database=safetyvision;User=safetyvision_app;Password=test;"
            }
        };

        Assert.Contains(options.Validate(), message => message.Contains("ListenAddress", StringComparison.Ordinal));
    }
}
