using Microsoft.EntityFrameworkCore;
using SafetyVision.Core.Analysis;
using SafetyVision.Core.Domain;
using SafetyVision.Data.Entities;

namespace SafetyVision.Data.Services;

public sealed record EquipmentRate(EquipmentCode Code, double? WornRatio);

public sealed record RecentInspectionRow(long Id, DateTime InspectedAtUtc, string CameraName, string Hardhat, string Vest, string Mask, string Result, bool HasImage);

public sealed record DashboardStats(
    int Total, int Normal, int CheckRequired, int Unconfirmed,
    IReadOnlyList<EquipmentRate> EquipmentRates,
    IReadOnlyList<RecentInspectionRow> Recent);

// 04_DB설계.md 8절: 모든 집계는 서버가 MySQL 쿼리로 계산한다.
public sealed class DashboardQueryService(SafetyVisionDbContext db)
{
    public async Task<DashboardStats> GetStatsAsync(CancellationToken ct)
    {
        int total = await db.Inspections.CountAsync(ct);
        int normal = await db.Inspections.CountAsync(i => i.Result == "NORMAL", ct);
        int check = await db.Inspections.CountAsync(i => i.Result == "CHECK_REQUIRED", ct);
        int unconfirmed = await db.Inspections.CountAsync(i => i.Result == "UNCONFIRMED", ct);

        var rates = new List<EquipmentRate>();
        foreach (var code in new[] { EquipmentCode.Hardhat, EquipmentCode.Vest, EquipmentCode.Mask })
        {
            string dbCode = EquipmentClassMap.ToDbCode(code);
            int itemTotal = await db.InspectionItems.CountAsync(x => x.EquipmentCode == dbCode, ct);
            if (itemTotal == 0)
            {
                rates.Add(new EquipmentRate(code, null));
                continue;
            }
            int worn = await db.InspectionItems.CountAsync(x => x.EquipmentCode == dbCode && x.Status == "WORN", ct);
            rates.Add(new EquipmentRate(code, (double)worn / itemTotal));
        }

        var recentEntities = await db.Inspections
            .OrderByDescending(i => i.InspectedAt).ThenByDescending(i => i.Id)
            .Take(5)
            .Include(i => i.Items)
            .ToListAsync(ct);

        var recent = recentEntities.Select(i => new RecentInspectionRow(
            i.Id, i.InspectedAt, i.CameraName,
            ItemStatus(i, "hardhat"), ItemStatus(i, "vest"), ItemStatus(i, "mask"),
            i.Result, !string.IsNullOrEmpty(i.ImagePath))).ToList();

        return new DashboardStats(total, normal, check, unconfirmed, rates, recent);
    }

    internal static string ItemStatus(Inspection i, string equipmentCode) =>
        i.Items.FirstOrDefault(x => x.EquipmentCode == equipmentCode)?.Status ?? "UNKNOWN";
}
