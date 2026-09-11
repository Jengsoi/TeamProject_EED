using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SafetyVision.Core.Domain;
using SafetyVision.Data;
using SafetyVision.Data.Services;
using Xunit;

namespace SafetyVision.Tests.Data;

// 06_개발일정및역할.md Day6 "저장 테스트": 동일 InspectionKey 재시도 중복 없음, 항목 정확히 3개.
[Collection("MySqlIntegration")]
public sealed class InspectionSaveServiceTests : IAsyncLifetime
{
    private SafetyVisionDbContext _db = null!;
    private InspectionSaveService _service = null!;

    public async Task InitializeAsync()
    {
        _db = TestDatabase.CreateContext();
        await TestDatabase.ResetAsync(_db);
        _service = new InspectionSaveService(_db, NullLogger<InspectionSaveService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await TestDatabase.ResetAsync(_db);
        await _db.DisposeAsync();
    }

    private static SaveInspectionRequest BuildRequest(Guid key) => new(
        key,
        DateTime.UtcNow,
        InspectionResultType.CheckRequired,
        [
            new EquipmentJudgement(EquipmentCode.Hardhat, EquipmentStatus.Worn, 0.8, 8, 2, 12),
            new EquipmentJudgement(EquipmentCode.Vest, EquipmentStatus.NotWorn, 0.75, 3, 9, 12),
            new EquipmentJudgement(EquipmentCode.Mask, EquipmentStatus.Unknown, null, 2, 2, 12),
        ],
        RepresentativeJpeg: null,
        PersonConfidence: 0.93,
        CameraName: "CAM 01",
        ModelName: "ayushgupta7777/safetyvision-yolov8",
        ModelVersion: "v2");

    [Fact]
    public async Task SaveAsync_CreatesExactlyThreeItems()
    {
        var key = Guid.NewGuid();
        var result = await _service.SaveAsync(BuildRequest(key), CancellationToken.None);

        Assert.Equal(SaveOutcome.Success, result.Outcome);
        Assert.NotNull(result.Id);

        var items = await _db.InspectionItems.Where(i => i.InspectionId == result.Id).ToListAsync();
        Assert.Equal(3, items.Count);
        Assert.Equal(["hardhat", "mask", "vest"], items.Select(i => i.EquipmentCode).OrderBy(c => c));
    }

    [Fact]
    public async Task SaveAsync_SameKeyTwice_DoesNotDuplicate()
    {
        var key = Guid.NewGuid();
        var first = await _service.SaveAsync(BuildRequest(key), CancellationToken.None);
        var second = await _service.SaveAsync(BuildRequest(key), CancellationToken.None);

        Assert.Equal(SaveOutcome.Success, first.Outcome);
        Assert.Equal(SaveOutcome.Success, second.Outcome);
        Assert.Equal(first.Id, second.Id);

        int count = await _db.Inspections.CountAsync(i => i.InspectionKey == key);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SaveAsync_DifferentKeys_CreateSeparateRows()
    {
        var r1 = await _service.SaveAsync(BuildRequest(Guid.NewGuid()), CancellationToken.None);
        var r2 = await _service.SaveAsync(BuildRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.NotEqual(r1.Id, r2.Id);
    }
}
