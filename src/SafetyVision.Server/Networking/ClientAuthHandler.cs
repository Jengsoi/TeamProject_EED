using Microsoft.Extensions.DependencyInjection;
using SafetyVision.Data.Services;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Server.Networking;

internal sealed class ClientAuthHandler(IServiceScopeFactory scopeFactory)
{
    public async Task<LoginResponsePayload> LoginAsync(LoginRequestPayload request, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var (success, displayName) = await auth
            .ValidateLoginAsync(request.LoginId, request.Password, ct).ConfigureAwait(false);
        return success
            ? new LoginResponsePayload(true, displayName, null)
            : new LoginResponsePayload(false, null, "아이디 또는 비밀번호를 확인해 주세요.");
    }
}
