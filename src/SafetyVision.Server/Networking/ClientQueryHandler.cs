using Microsoft.Extensions.DependencyInjection;
using SafetyVision.Core.Analysis;
using SafetyVision.Data.Services;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Server.Networking;

internal sealed class ClientQueryHandler(IServiceScopeFactory scopeFactory)
{
    public async Task<DashboardStatsResponsePayload> GetDashboardAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var stats = await scope.ServiceProvider.GetRequiredService<DashboardQueryService>()
            .GetStatsAsync(ct).ConfigureAwait(false);
        return new DashboardStatsResponsePayload(
            stats.Total, stats.Normal, stats.CheckRequired, stats.Unconfirmed,
            stats.EquipmentRates.Select(r =>
                new EquipmentRatePayload(EquipmentClassMap.ToDbCode(r.Code), r.WornRatio)).ToList(),
            stats.Recent.Select(r => new RecentInspectionPayload(
                r.Id, AsUtcOffset(r.InspectedAtUtc),
                string.IsNullOrEmpty(r.CameraName) ? "CAM 01" : r.CameraName,
                r.Hardhat, r.Vest, r.Mask, r.Result, r.HasImage)).ToList());
    }

    public async Task<StatisticsResponsePayload> GetStatisticsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dashboard = scope.ServiceProvider.GetRequiredService<DashboardQueryService>();
        var statistics = scope.ServiceProvider.GetRequiredService<StatisticsQueryService>();
        var stats = await dashboard.GetStatsAsync(ct).ConfigureAwait(false);
        var breakdown = await statistics.GetEquipmentBreakdownAsync(ct).ConfigureAwait(false);
        var daily = await statistics.GetDailyTrendAsync(14, ct).ConfigureAwait(false);
        var monthly = await statistics.GetMonthlyTrendAsync(6, ct).ConfigureAwait(false);
        var cameras = await statistics.GetCameraBreakdownAsync(ct).ConfigureAwait(false);
        return new StatisticsResponsePayload(
            stats.Total, stats.Normal, stats.CheckRequired + stats.Unconfirmed,
            breakdown.Select(b => new EquipmentBreakdownPayload(
                EquipmentClassMap.ToDbCode(b.Code), b.Worn, b.NotWorn, b.Unknown)).ToList(),
            daily.Select(d => new DailyTrendPointPayload(d.Date, d.Normal, d.CheckRequired)).ToList(),
            monthly.Select(m => new MonthlyTrendPointPayload(m.Year, m.Month, m.Normal, m.CheckRequired)).ToList(),
            cameras.Select(c => new CameraBreakdownPayload(
                c.CameraName, c.Total, c.Normal, c.CheckRequired)).ToList());
    }

    public async Task<HistoryPageResponsePayload> GetHistoryAsync(
        HistoryPageRequestPayload request, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<HistoryQueryService>()
            .GetPageAsync(request.Page, request.FromUtc?.UtcDateTime, request.ToUtc?.UtcDateTime,
                request.ResultFilter, ct).ConfigureAwait(false);
        return new HistoryPageResponsePayload(page.Page, page.TotalPages, page.TotalCount,
            page.Rows.Select(r => new HistoryRowPayload(
                r.Id, AsUtcOffset(r.InspectedAtUtc), r.Hardhat, r.Vest, r.Mask, r.Result)).ToList());
    }

    public async Task<InspectionDetailQueryResult?> GetInspectionDetailAsync(long id, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var history = scope.ServiceProvider.GetRequiredService<HistoryQueryService>();
        var saveService = scope.ServiceProvider.GetRequiredService<InspectionSaveService>();
        var inspection = await history.GetDetailAsync(id, ct).ConfigureAwait(false);
        if (inspection is null) return null;

        byte[]? image = null;
        string? imageError = "이미지 파일을 찾을 수 없습니다.";
        if (!string.IsNullOrEmpty(inspection.ImagePath))
        {
            var fullPath = saveService.ResolveImageFullPath(inspection.ImagePath);
            if (File.Exists(fullPath))
            {
                image = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
                imageError = null;
            }
        }

        var payload = new InspectionDetailResponsePayload(
            inspection.Id, AsUtcOffset(inspection.InspectedAt),
            inspection.Items.Select(i =>
                new EquipmentResultPayload(i.EquipmentCode, i.Status, i.Score)).ToList(),
            inspection.Result, image is not null, imageError);
        return new InspectionDetailQueryResult(payload, image);
    }

    private static DateTimeOffset AsUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

internal sealed record InspectionDetailQueryResult(
    InspectionDetailResponsePayload Payload,
    byte[]? Image);
