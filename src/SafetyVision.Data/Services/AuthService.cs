using Microsoft.EntityFrameworkCore;
using SafetyVision.Data.Security;

namespace SafetyVision.Data.Services;

public sealed class AuthService(SafetyVisionDbContext db)
{
    public async Task<(bool Success, string? DisplayName)> ValidateLoginAsync(string loginId, string password, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.LoginId == loginId, ct);
        if (user is null) return (false, null);
        return PasswordHasher.Verify(password, user.PasswordHash) ? (true, user.Name) : (false, null);
    }
}
