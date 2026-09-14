using Microsoft.EntityFrameworkCore;
using SafetyVision.Data;
using SafetyVision.Data.Entities;
using SafetyVision.Data.Security;
using SafetyVision.Data.Seeding;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Server.Features;

// 사용자 관리 요청은 이 서비스에서만 검증하고, 비밀번호는 해시값으로만 저장한다.
public sealed class UserManagementService(SafetyVisionDbContext db)
{
    public async Task<UserListResponsePayload> ListAsync(CancellationToken ct)
    {
        var rows = await db.Users
            .AsNoTracking()
            .OrderBy(user => user.LoginId)
            .Select(user => new { user.Id, user.LoginId, user.Name, user.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var users = rows
            .Select(row => new UserSummaryPayload(
                row.Id,
                row.LoginId,
                row.Name,
                ToUtcOffset(row.CreatedAt)))
            .ToList();

        return new UserListResponsePayload(users);
    }

    public async Task<UserMutationResponsePayload> CreateAsync(UserCreateRequestPayload request, CancellationToken ct)
    {
        string loginId = request.LoginId?.Trim() ?? string.Empty;
        string name = request.Name?.Trim() ?? string.Empty;
        string password = request.Password ?? string.Empty;

        var validationFailure = ValidateNewUser(loginId, name, password);
        if (validationFailure is not null) return Failure(validationFailure);

        if (await db.Users.AnyAsync(user => user.LoginId == loginId, ct).ConfigureAwait(false))
            return Failure("이미 사용 중인 아이디입니다.");

        var user = new User
        {
            LoginId = loginId,
            Name = name,
            PasswordHash = PasswordHasher.Hash(password),
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsDuplicateLoginIdViolation(exception))
        {
            // 사전 조회와 저장 사이에 같은 아이디가 만들어진 경우에도 DB 예외를 노출하지 않는다.
            db.Entry(user).State = EntityState.Detached;
            return Failure("이미 사용 중인 아이디입니다.");
        }

        return new UserMutationResponsePayload(true, "사용자를 추가했습니다.", user.Id);
    }

    public async Task<UserMutationResponsePayload> ChangePasswordAsync(UserPasswordChangeRequestPayload request, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == request.Id, ct).ConfigureAwait(false);
        if (user is null) return Failure("사용자를 찾을 수 없습니다.");

        string password = request.NewPassword ?? string.Empty;
        if (password.Length < 8) return Failure("비밀번호는 8자 이상이어야 합니다.");

        user.PasswordHash = PasswordHasher.Hash(password);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new UserMutationResponsePayload(true, "비밀번호를 변경했습니다.", user.Id);
    }

    public async Task<UserMutationResponsePayload> DeleteAsync(UserDeleteRequestPayload request, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == request.Id, ct).ConfigureAwait(false);
        if (user is null) return Failure("사용자를 찾을 수 없습니다.");

        if (string.Equals(user.LoginId, AdminSeeder.AdminLoginId, StringComparison.Ordinal))
            return Failure("admin 계정은 삭제할 수 없습니다.");

        if (await db.Users.CountAsync(ct).ConfigureAwait(false) <= 1)
            return Failure("마지막 사용자는 삭제할 수 없습니다.");

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new UserMutationResponsePayload(true, "사용자를 삭제했습니다.", user.Id);
    }

    private static string? ValidateNewUser(string loginId, string name, string password)
    {
        if (loginId.Length == 0) return "아이디를 입력해 주세요.";
        if (loginId.Length < 3 || loginId.Length > 64) return "아이디는 3자 이상 64자 이하여야 합니다.";
        if (name.Length == 0) return "이름을 입력해 주세요.";
        if (name.Length > 100) return "이름은 100자 이하여야 합니다.";
        if (password.Length < 8) return "비밀번호는 8자 이상이어야 합니다.";
        return null;
    }

    private static UserMutationResponsePayload Failure(string message) => new(false, message, null);

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static bool IsDuplicateLoginIdViolation(DbUpdateException exception)
    {
        // Pomelo/MySqlConnector의 duplicate-key 오류 번호는 1062이다. 직접 패키지 의존성을
        // 추가하지 않고도 내부 예외의 Number 속성으로 식별해 서버 프로젝트의 참조를 유지한다.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name != "MySqlException") continue;

            var number = current.GetType().GetProperty("Number")?.GetValue(current);
            if (number is int { } value && value == 1062) return true;
        }

        return false;
    }
}
