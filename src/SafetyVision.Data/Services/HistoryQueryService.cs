using Microsoft.EntityFrameworkCore;
using SafetyVision.Data.Entities;

namespace SafetyVision.Data.Services;

public sealed record HistoryRow(long Id, DateTime InspectedAtUtc, string Hardhat, string Vest, string Mask, string Result);

public sealed record HistoryPage(int Page, int TotalPages, int TotalCount, IReadOnlyList<HistoryRow> Rows);

// 04_DB설계.md 8절 / 02_요구사항.md FR-10: 최신순, 페이지당 50건.
public sealed class HistoryQueryService(SafetyVisionDbContext db)
{
    private const int PageSize = 50;

    public async Task<HistoryPage> GetPageAsync(int page, DateTime? fromUtc, DateTime? toUtc, string? resultFilter, CancellationToken ct)
    {
        page = Math.Max(1, page);
        var query = db.Inspections.AsQueryable();
        if (fromUtc is not null) query = query.Where(i => i.InspectedAt >= fromUtc);
        if (toUtc is not null) query = query.Where(i => i.InspectedAt <= toUtc);
        if (!string.IsNullOrEmpty(resultFilter)) query = query.Where(i => i.Result == resultFilter);

        int totalCount = await query.CountAsync(ct);
        int totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling(totalCount / (double)PageSize);
        page = Math.Min(page, totalPages);

        var entities = await query
            .OrderByDescending(i => i.InspectedAt).ThenByDescending(i => i.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Include(i => i.Items)
            .ToListAsync(ct);

        var rows = entities.Select(i => new HistoryRow(
            i.Id, i.InspectedAt,
            DashboardQueryService.ItemStatus(i, "hardhat"),
            DashboardQueryService.ItemStatus(i, "vest"),
            DashboardQueryService.ItemStatus(i, "mask"),
            i.Result)).ToList();

        return new HistoryPage(page, totalPages, totalCount, rows);
    }

    public Task<Inspection?> GetDetailAsync(long id, CancellationToken ct) =>
        db.Inspections.Include(i => i.Items).FirstOrDefaultAsync(i => i.Id == id, ct);
}
