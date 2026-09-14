using Microsoft.EntityFrameworkCore;
using SafetyVision.Core.Analysis;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using SafetyVision.Data;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Server.Features;

// 장비 관리 화면은 설정과 누적 판정 통계를 읽기 전용으로 제공한다.
public sealed class EquipmentManagementService(SafetyVisionOptions options, SafetyVisionDbContext db)
{
    public async Task<EquipmentManagementResponsePayload> HandleAsync(EquipmentManagementRequestPayload request, CancellationToken ct)
    {
        // 스키마상 허용된 세 장비만 데이터베이스에서 집계한다. AVG(Score)는 SQL에서 NULL을 제외한다.
        var aggregateRows = await db.InspectionItems
            .AsNoTracking()
            .Where(item => item.EquipmentCode == "hardhat"
                || item.EquipmentCode == "vest"
                || item.EquipmentCode == "mask")
            .GroupBy(item => item.EquipmentCode)
            .Select(group => new
            {
                EquipmentCode = group.Key,
                Worn = group.Count(item => item.Status == "WORN"),
                NotWorn = group.Count(item => item.Status == "NOT_WORN"),
                Unknown = group.Count(item => item.Status == "UNKNOWN"),
                Total = group.Count(),
                AverageScore = group.Average(item => item.Score)
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var usageByCode = aggregateRows.ToDictionary(row => row.EquipmentCode, StringComparer.Ordinal);
        var rules = new[]
        {
            CreateRule(EquipmentCode.Hardhat, "안전모"),
            CreateRule(EquipmentCode.Vest, "안전조끼"),
            CreateRule(EquipmentCode.Mask, "마스크")
        };

        var usage = new List<EquipmentUsagePayload>(rules.Length);
        foreach (var rule in rules)
        {
            if (!usageByCode.TryGetValue(rule.EquipmentCode, out var aggregate))
            {
                usage.Add(new EquipmentUsagePayload(rule.EquipmentCode, 0, 0, 0, 0, null, null));
                continue;
            }

            usage.Add(new EquipmentUsagePayload(
                rule.EquipmentCode,
                aggregate.Worn,
                aggregate.NotWorn,
                aggregate.Unknown,
                aggregate.Total,
                aggregate.Total == 0 ? null : (double)aggregate.Worn / aggregate.Total,
                aggregate.AverageScore));
        }

        return new EquipmentManagementResponsePayload(
            options.DetectionConfidence,
            options.NmsIouThreshold,
            options.MinEvidenceFrames,
            options.MinEvidenceRatio,
            options.DecisionRatio,
            options.MinAnalysisFrames,
            options.TargetAnalysisFrames,
            options.ModelName,
            options.ModelVersion,
            rules,
            usage);
    }

    private EquipmentRulePayload CreateRule(EquipmentCode code, string displayName)
    {
        var classes = EquipmentClassMap.All.Single(pair => pair.Code == code);
        var (regionTopRatio, regionBottomRatio) = code switch
        {
            // PpeAssociationRules는 person.Y - HeadTopMarginRatio * height를 하한으로 사용한다.
            EquipmentCode.Hardhat => (-options.HeadTopMarginRatio, options.HardhatBottomRatio),
            EquipmentCode.Mask => (-options.HeadTopMarginRatio, options.MaskBottomRatio),
            EquipmentCode.Vest => (options.VestTopRatio, options.VestBottomRatio),
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };

        return new EquipmentRulePayload(
            EquipmentClassMap.ToDbCode(code),
            displayName,
            [classes.Positive.ToString()],
            [classes.Negative.ToString()],
            regionTopRatio,
            regionBottomRatio,
            options.PersonHorizontalMarginRatio);
    }
}
