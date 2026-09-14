using SafetyVision.Data;
using SafetyVision.Data.Entities;
using SafetyVision.Data.Services;
using Xunit;

namespace SafetyVision.Tests.Data;

// 06_개발일정및역할.md Day6 "통계 테스트": UNKNOWN 포함 분모, 전체 합계, 0건 처리.
// 04_DB설계.md 8절: 착용률 분모에 NOT_WORN과 UNKNOWN을 모두 포함한다.
[Collection("MySqlIntegration")]
public sealed class DashboardQueryServiceTests : IAsyncLifetime
{
    private SafetyVisionDbContext _db = null!;
    private DashboardQueryService _service = null!;

    public async Task InitializeAsync()
    {
        _db = TestDatabase.CreateContext();
        await TestDatabase.ResetAsync(_db);
        _service = new DashboardQueryService(_db);
    }

    public async Task DisposeAsync()
    {
        await TestDatabase.ResetAsync(_db);
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task GetStatsAsync_EmptyDatabase_ReturnsZerosAndNullRatios()
    {
        var stats = await _service.GetStatsAsync(CancellationToken.None);

        Assert.Equal(0, stats.Total);
        Assert.Equal(0, stats.Normal);
        Assert.Equal(0, stats.CheckRequired);
        Assert.Equal(0, stats.Unconfirmed);
        Assert.All(stats.EquipmentRates, r => Assert.Null(r.WornRatio));
        Assert.Empty(stats.Recent);
    }

    [Fact]
    public async Task GetStatsAsync_IncludesNotWornAndUnknownInDenominator()
    {
        // hardhat: WORN, NOT_WORN, UNKNOWN 각 1건씩 -> 착용률 1/3
        await SeedInspectionAsync("NORMAL", hardhat: "WORN");
        await SeedInspectionAsync("CHECK_REQUIRED", hardhat: "NOT_WORN");
        await SeedInspectionAsync("UNCONFIRMED", hardhat: "UNKNOWN");

        var stats = await _service.GetStatsAsync(CancellationToken.None);

        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.Normal);
        Assert.Equal(1, stats.CheckRequired);
        Assert.Equal(1, stats.Unconfirmed);

        var hardhatRate = stats.EquipmentRates.Single(r => r.Code == SafetyVision.Core.Domain.EquipmentCode.Hardhat);
        Assert.NotNull(hardhatRate.WornRatio);
        Assert.Equal(1.0 / 3.0, hardhatRate.WornRatio!.Value, precision: 10);
    }

    [Fact]
    public async Task GetStatsAsync_RecentTakesLatest5OrderedByInspectedAtDesc()
    {
        for (int i = 0; i < 12; i++)
        {
            await SeedInspectionAsync("NORMAL", hardhat: "WORN", inspectedAt: DateTime.UtcNow.AddMinutes(-i));
        }

        var stats = await _service.GetStatsAsync(CancellationToken.None);

        Assert.Equal(12, stats.Total);
        Assert.Equal(5, stats.Recent.Count);
        // 가장 최근(inspectedAt 가장 큼, i=0)이 먼저 와야 한다.
        Assert.True(stats.Recent[0].InspectedAtUtc >= stats.Recent[^1].InspectedAtUtc);
    }

    private async Task SeedInspectionAsync(string result, string hardhat, DateTime? inspectedAt = null)
    {
        var inspection = new Inspection
        {
            InspectionKey = Guid.NewGuid(),
            InspectedAt = inspectedAt ?? DateTime.UtcNow,
            Result = result,
            ImagePath = "",
            ModelName = "ayushgupta7777/safetyvision-yolov8",
            ModelVersion = "v2",
            CreatedAt = DateTime.UtcNow,
        };
        inspection.Items.Add(new InspectionItem { EquipmentCode = "hardhat", Status = hardhat, PositiveFrames = 0, NegativeFrames = 0, TotalFrames = 12, CreatedAt = DateTime.UtcNow });
        inspection.Items.Add(new InspectionItem { EquipmentCode = "vest", Status = "WORN", PositiveFrames = 10, NegativeFrames = 0, TotalFrames = 12, CreatedAt = DateTime.UtcNow });
        inspection.Items.Add(new InspectionItem { EquipmentCode = "mask", Status = "WORN", PositiveFrames = 10, NegativeFrames = 0, TotalFrames = 12, CreatedAt = DateTime.UtcNow });

        _db.Inspections.Add(inspection);
        await _db.SaveChangesAsync();
    }
}
