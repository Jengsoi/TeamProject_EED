using Microsoft.EntityFrameworkCore;
using SafetyVision.Data;
using SafetyVision.Data.Entities;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Server.Features;

/// <summary>
/// Returns date-filtered statistics. The upper bound is exclusive, which keeps
/// local-day selection correct after the client converts it to UTC.
/// </summary>
public sealed class StatisticsService(SafetyVisionDbContext db)
{
    public async Task<StatisticsResponsePayload> HandleAsync(
        StatisticsRequestPayload request,
        CancellationToken ct)
    {
        DateTime? fromUtc = request.FromUtc?.UtcDateTime;
        DateTime? toExclusiveUtc = request.ToUtc?.UtcDateTime;

        if (fromUtc is not null && toExclusiveUtc is not null && toExclusiveUtc <= fromUtc)
            return Empty();

        IQueryable<Inspection> inspections = db.Inspections.AsNoTracking();
        if (fromUtc is not null)
            inspections = inspections.Where(i => i.InspectedAt >= fromUtc.Value);
        if (toExclusiveUtc is not null)
            inspections = inspections.Where(i => i.InspectedAt < toExclusiveUtc.Value);

        int total = await inspections.CountAsync(ct).ConfigureAwait(false);
        int normal = await inspections.CountAsync(i => i.Result == "NORMAL", ct).ConfigureAwait(false);
        int checkRequired = await inspections.CountAsync(i => i.Result != "NORMAL", ct).ConfigureAwait(false);

        DateTime? first = total == 0
            ? null
            : await inspections.MinAsync(i => i.InspectedAt, ct).ConfigureAwait(false);
        DateTime? last = total == 0
            ? null
            : await inspections.MaxAsync(i => i.InspectedAt, ct).ConfigureAwait(false);

        var itemRows = await (
                from item in db.InspectionItems.AsNoTracking()
                join inspection in inspections on item.InspectionId equals inspection.Id
                group item by new { item.EquipmentCode, item.Status }
                into grouped
                select new
                {
                    grouped.Key.EquipmentCode,
                    grouped.Key.Status,
                    Count = grouped.Count()
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var itemCounts = itemRows.ToDictionary(
            row => (row.EquipmentCode, row.Status),
            row => row.Count);
        int CountItem(string code, string status) =>
            itemCounts.GetValueOrDefault((code, status));

        var breakdown = new[] { "hardhat", "vest", "mask" }
            .Select(code => new EquipmentBreakdownPayload(
                code,
                CountItem(code, "WORN"),
                CountItem(code, "NOT_WORN"),
                CountItem(code, "UNKNOWN")))
            .ToList();

        var dailyRows = await inspections
            .GroupBy(i => new { i.InspectedAt.Year, i.InspectedAt.Month, i.InspectedAt.Day })
            .Select(grouped => new
            {
                grouped.Key.Year,
                grouped.Key.Month,
                grouped.Key.Day,
                Normal = grouped.Count(i => i.Result == "NORMAL"),
                CheckRequired = grouped.Count(i => i.Result != "NORMAL")
            })
            .OrderBy(row => row.Year).ThenBy(row => row.Month).ThenBy(row => row.Day)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var monthlyRows = await inspections
            .GroupBy(i => new { i.InspectedAt.Year, i.InspectedAt.Month })
            .Select(grouped => new
            {
                grouped.Key.Year,
                grouped.Key.Month,
                Normal = grouped.Count(i => i.Result == "NORMAL"),
                CheckRequired = grouped.Count(i => i.Result != "NORMAL")
            })
            .OrderBy(row => row.Year).ThenBy(row => row.Month)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var cameraRowData = await inspections
            .GroupBy(i => i.CameraName == null || i.CameraName == "" ? "CAM 01" : i.CameraName)
            .Select(grouped => new
            {
                CameraName = grouped.Key,
                Total = grouped.Count(),
                Normal = grouped.Count(i => i.Result == "NORMAL"),
                CheckRequired = grouped.Count(i => i.Result != "NORMAL")
            })
            .OrderBy(row => row.CameraName)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var cameraRows = cameraRowData
            .Select(row => new CameraBreakdownPayload(
                row.CameraName,
                row.Total,
                row.Normal,
                row.CheckRequired))
            .ToList();

        var daily = dailyRows
            .Select(row => new DailyTrendPointPayload(
                new DateOnly(row.Year, row.Month, row.Day),
                row.Normal,
                row.CheckRequired))
            .ToList();
        var monthly = monthlyRows
            .Select(row => new MonthlyTrendPointPayload(
                row.Year,
                row.Month,
                row.Normal,
                row.CheckRequired))
            .ToList();

        return new StatisticsResponsePayload(
            total,
            normal,
            checkRequired,
            breakdown,
            daily,
            monthly,
            cameraRows,
            total == 0 ? null : (double)normal / total,
            first is null ? null : ToUtcOffset(first.Value),
            last is null ? null : ToUtcOffset(last.Value));
    }

    private static StatisticsResponsePayload Empty() => new(
        0,
        0,
        0,
        [
            new EquipmentBreakdownPayload("hardhat", 0, 0, 0),
            new EquipmentBreakdownPayload("vest", 0, 0, 0),
            new EquipmentBreakdownPayload("mask", 0, 0, 0)
        ],
        [],
        [],
        []);

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
