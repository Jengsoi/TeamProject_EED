using Microsoft.EntityFrameworkCore;
using SafetyVision.Data.Entities;
using SafetyVision.Data.Security;

namespace SafetyVision.Data.Seeding;

public static class AdminSeeder
{
    public const string AdminLoginId = "admin";
    public const string AdminInitialPassword = "SafetyVision!2026";

    // 최초 생성 시 1회만 Seed. 계정이 이미 있으면 비밀번호를 재설정하거나 중복 생성하지 않는다.
    public static async Task EnsureSeedAdminAsync(SafetyVisionDbContext db, CancellationToken ct = default)
    {
        bool exists = await db.Users.AnyAsync(u => u.LoginId == AdminLoginId, ct);
        if (exists) return;

        db.Users.Add(new User
        {
            LoginId = AdminLoginId,
            PasswordHash = PasswordHasher.Hash(AdminInitialPassword),
            Name = "관리자",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }
}
