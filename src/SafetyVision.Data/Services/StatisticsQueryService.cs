using Microsoft.EntityFrameworkCore;
using SafetyVision.Core.Analysis;
using SafetyVision.Core.Domain;

namespace SafetyVision.Data.Services;

public sealed record EquipmentStatusCount(EquipmentCode Code, int Worn, int NotWorn, int Unknown);
public sealed record DailyTrendPoint(DateOnly Date, int Normal, int CheckRequired);
public sealed record MonthlyTrendPoint(int Year, int Month, int Normal, int CheckRequired);
public sealed record CameraStatusCount(string CameraName, int Total, int Normal, int CheckRequired);

// 통계 분석 화면 전용: 대시보드보다 상세한 장비별 착용/미착용/미확인 건수, 일별/월별 추이, 카메라별 현황을 제공한다.
// 지금은 카메라가 1대뿐이라 결과가 항상 한 줄이지만, 카메라 구분값(CameraName)은 검사마다 DB에 저장되므로
// 카메라가 늘어나도 이 쿼리는 그대로 동작한다.
public sealed class StatisticsQueryService(SafetyVisionDbContext db)
{
    public async Task<IReadOnlyList<CameraStatusCount>> GetCameraBreakdownAsync(CancellationToken ct)
    {
        var rows = await db.Inspections
            .GroupBy(i => i.CameraName == null || i.CameraName == "" ? "CAM 01" : i.CameraName)
            .Select(g => new
            {
                CameraName = g.Key,
                Total = g.Count(),
                Normal = g.Count(i => i.Result == "NORMAL"),
                CheckRequired = g.Count(i => i.Result != "NORMAL"),
            })
            .OrderBy(c => c.CameraName)
            .ToListAsync(ct);

        return rows
            .Select(c => new CameraStatusCount(c.CameraName, c.Total, c.Normal, c.CheckRequired))
            .OrderBy(c => c.CameraName)
            .ToList();
    }

    public async Task<IReadOnlyList<EquipmentStatusCount>> GetEquipmentBreakdownAsync(CancellationToken ct)
    {
        var codes = EquipmentClassMap.All.Select(p => EquipmentClassMap.ToDbCode(p.Code)).ToArray();
        var grouped = await db.InspectionItems
            .Where(i => codes.Contains(i.EquipmentCode))
            .GroupBy(i => new { i.EquipmentCode, i.Status })
            .Select(g => new { g.Key.EquipmentCode, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var result = new List<EquipmentStatusCount>(EquipmentClassMap.All.Count);
        foreach (var pair in EquipmentClassMap.All)
        {
            string dbCode = EquipmentClassMap.ToDbCode(pair.Code);
            int Count(string status) => grouped.FirstOrDefault(x => x.EquipmentCode == dbCode && x.Status == status)?.Count ?? 0;
            result.Add(new EquipmentStatusCount(pair.Code, Count("WORN"), Count("NOT_WORN"), Count("UNKNOWN")));
        }
        return result;
    }

    public async Task<IReadOnlyList<DailyTrendPoint>> GetDailyTrendAsync(int days, CancellationToken ct)
    {
        var sinceUtc = DateTime.UtcNow.AddDays(-days);
        var rows = await db.Inspections
            .Where(i => i.InspectedAt >= sinceUtc)
            .Select(i => new { i.InspectedAt, i.Result })
            .ToListAsync(ct);

        var byDate = rows
            .Select(r => new { Local = DateTime.SpecifyKind(r.InspectedAt, DateTimeKind.Utc).ToLocalTime(), r.Result })
            .GroupBy(r => DateOnly.FromDateTime(r.Local))
            .ToDictionary(g => g.Key, g => (Normal: g.Count(x => x.Result == "NORMAL"), Other: g.Count(x => x.Result != "NORMAL")));

        var today = DateOnly.FromDateTime(DateTime.Now);
        var result = new List<DailyTrendPoint>();
        for (int i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            var (normal, other) = byDate.TryGetValue(date, out var v) ? v : (0, 0);
            result.Add(new DailyTrendPoint(date, normal, other));
        }
        return result;
    }

    public async Task<IReadOnlyList<MonthlyTrendPoint>> GetMonthlyTrendAsync(int months, CancellationToken ct)
    {
        var sinceUtc = DateTime.UtcNow.AddMonths(-months);
        var rows = await db.Inspections
            .Where(i => i.InspectedAt >= sinceUtc)
            .Select(i => new { i.InspectedAt, i.Result })
            .ToListAsync(ct);

        var byMonth = rows
            .Select(r => new { Local = DateTime.SpecifyKind(r.InspectedAt, DateTimeKind.Utc).ToLocalTime(), r.Result })
            .GroupBy(r => new { r.Local.Year, r.Local.Month })
            .ToDictionary(g => (g.Key.Year, g.Key.Month), g => (Normal: g.Count(x => x.Result == "NORMAL"), Other: g.Count(x => x.Result != "NORMAL")));

        var nowLocal = DateTime.Now;
        var result = new List<MonthlyTrendPoint>();
        for (int i = months - 1; i >= 0; i--)
        {
            var d = nowLocal.AddMonths(-i);
            var (normal, other) = byMonth.TryGetValue((d.Year, d.Month), out var v) ? v : (0, 0);
            result.Add(new MonthlyTrendPoint(d.Year, d.Month, normal, other));
        }
        return result;
    }
}
