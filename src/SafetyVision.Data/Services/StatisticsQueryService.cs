using Microsoft.EntityFrameworkCore;
using SafetyVision.Core.Analysis;
using SafetyVision.Core.Domain;

namespace SafetyVision.Data.Services;

public sealed record EquipmentStatusCount(EquipmentCode Code, int Worn, int NotWorn, int Unknown);

// 통계 분석 화면 전용: 대시보드보다 상세한 장비별 착용/미착용/미확인 건수를 제공한다.
public sealed class StatisticsQueryService(SafetyVisionDbContext db)
{
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
}
