using SafetyVision.Data.Security;
using Xunit;

namespace SafetyVision.Tests.Data;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_ThenVerify_WithCorrectPassword_Succeeds()
    {
        var encoded = PasswordHasher.Hash("SafetyVision!2026");
        Assert.True(PasswordHasher.Verify("SafetyVision!2026", encoded));
    }

    [Fact]
    public void Verify_WithWrongPassword_Fails()
    {
        var encoded = PasswordHasher.Hash("SafetyVision!2026");
        Assert.False(PasswordHasher.Verify("wrong-password", encoded));
    }

    [Fact]
    public void Hash_ProducesDifferentSaltEachTime()
    {
        var a = PasswordHasher.Hash("same-password");
        var b = PasswordHasher.Hash("same-password");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Verify_WithMalformedEncodedString_ReturnsFalse()
    {
        Assert.False(PasswordHasher.Verify("anything", "not-a-valid-hash"));
    }
}
