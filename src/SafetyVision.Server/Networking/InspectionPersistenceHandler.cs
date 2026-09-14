using Microsoft.Extensions.DependencyInjection;
using SafetyVision.Data.Services;

namespace SafetyVision.Server.Networking;

internal sealed class InspectionPersistenceHandler(IServiceScopeFactory scopeFactory)
{
    public async Task<SaveInspectionResult> SaveAsync(SaveInspectionRequest request, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InspectionSaveService>()
            .SaveAsync(request, ct).ConfigureAwait(false);
    }
}
