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
            .Select(i => new { i.CameraName, i.Result })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => string.IsNullOrEmpty(r.CameraName) ? "CAM 01" : r.CameraName)
            .Select(g => new CameraStatusCount(g.Key, g.Count(), g.Count(x => x.Result == "NORMAL"), g.Count(x => x.Result != "NORMAL")))
            .OrderBy(c => c.CameraName)
            .ToList();
    }

    public async Task<IReadOnlyList<EquipmentStatusCount>> GetEquipmentBreakdownAsync(CancellationToken ct)
    {
        var result = new List<EquipmentStatusCount>();
        foreach (var code in new[] { EquipmentCode.Hardhat, EquipmentCode.Vest, EquipmentCode.Mask })
        {
            string dbCode = EquipmentClassMap.ToDbCode(code);
            int worn = await db.InspectionItems.CountAsync(x => x.EquipmentCode == dbCode && x.Status == "WORN", ct);
            int notWorn = await db.InspectionItems.CountAsync(x => x.EquipmentCode == dbCode && x.Status == "NOT_WORN", ct);
            int unknown = await db.InspectionItems.CountAsync(x => x.EquipmentCode == dbCode && x.Status == "UNKNOWN", ct);
            result.Add(new EquipmentStatusCount(code, worn, notWorn, unknown));
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
