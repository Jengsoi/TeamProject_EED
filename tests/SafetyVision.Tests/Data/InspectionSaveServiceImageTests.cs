using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SafetyVision.Core.Domain;
using SafetyVision.Data;
using SafetyVision.Data.Services;
using Xunit;

namespace SafetyVision.Tests.Data;

[Collection("MySqlIntegration")]
public sealed class InspectionSaveServiceImageTests : IAsyncLifetime
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xD9];

    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), "SafetyVisionTests", Guid.NewGuid().ToString("N"));
    private SafetyVisionDbContext _db = null!;
    private InspectionSaveService _service = null!;

    public async Task InitializeAsync()
    {
        _db = TestDatabase.CreateContext();
        await TestDatabase.ResetAsync(_db);
        _service = new InspectionSaveService(_db, NullLogger<InspectionSaveService>.Instance, _dataRoot);
    }

    public async Task DisposeAsync()
    {
        await TestDatabase.ResetAsync(_db);
        await _db.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static SaveInspectionRequest BuildRequest(Guid key, DateTime inspectedAtUtc) => new(
        key,
        inspectedAtUtc,
        InspectionResultType.Normal,
        [
            new EquipmentJudgement(EquipmentCode.Hardhat, EquipmentStatus.Worn, 1.0, 12, 0, 12),
            new EquipmentJudgement(EquipmentCode.Vest, EquipmentStatus.Worn, 1.0, 12, 0, 12),
            new EquipmentJudgement(EquipmentCode.Mask, EquipmentStatus.Worn, 1.0, 12, 0, 12),
        ],
        RepresentativeJpeg: Jpeg,
        PersonConfidence: 0.9,
        CameraName: "CAM 01",
        ModelName: "ayushgupta7777/safetyvision-yolov8",
        ModelVersion: "v2");

    private async Task<(long LastId, string SnapshotDir, string DateDir)> SaveBaselineAsync(DateTime inspectedAtUtc)
    {
        var baseline = await _service.SaveAsync(
            BuildRequest(Guid.NewGuid(), inspectedAtUtc), CancellationToken.None);
        Assert.Equal(SaveOutcome.Success, baseline.Outcome);

        string dateDir = inspectedAtUtc.ToLocalTime().ToString("yyyyMMdd");
        return (
            baseline.Id!.Value,
            Path.Combine(_dataRoot, "snapshots", dateDir),
            dateDir);
    }

    [Fact]
    public async Task SaveAsync_OldSnapshotWithSameId_KeepsOldFileAndSavesUnderInspectionKeyName()
    {
        var inspectedAt = DateTime.UtcNow;
        var (lastId, snapshotDir, dateDir) = await SaveBaselineAsync(inspectedAt);

        byte[] oldBytes = [1, 2, 3];
        for (long id = lastId + 1; id <= lastId + 20; id++)
            await File.WriteAllBytesAsync(Path.Combine(snapshotDir, $"inspection_{id}.jpg"), oldBytes);

        var key = Guid.NewGuid();
        var result = await _service.SaveAsync(
            BuildRequest(key, inspectedAt), CancellationToken.None);

        Assert.Equal(SaveOutcome.Success, result.Outcome);
        var saved = await _db.Inspections.AsNoTracking()
            .SingleAsync(i => i.InspectionKey == key);
        Assert.Equal(
            Path.Combine("snapshots", dateDir, $"inspection_{saved.Id}_{key:N}.jpg"),
            saved.ImagePath);
        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(_service.ResolveImageFullPath(saved.ImagePath)));
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(
            Path.Combine(snapshotDir, $"inspection_{saved.Id}.jpg")));
    }

    [Fact]
    public async Task SaveAsync_ImageFinalizeFailsAfterInsert_RollsBackRowsAndRemovesTempFile()
    {
        var inspectedAt = DateTime.UtcNow;
        var (lastId, snapshotDir, _) = await SaveBaselineAsync(inspectedAt);

        for (long id = lastId + 1; id <= lastId + 20; id++)
            Directory.CreateDirectory(Path.Combine(snapshotDir, $"inspection_{id}.jpg"));

        var key = Guid.NewGuid();
        var result = await _service.SaveAsync(
            BuildRequest(key, inspectedAt), CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Null(result.Id);
        Assert.Equal(0, await _db.Inspections.CountAsync(i => i.InspectionKey == key));
        Assert.Equal(1, await _db.Inspections.CountAsync());
        Assert.Equal(3, await _db.InspectionItems.CountAsync());
        Assert.Empty(Directory.GetFiles(snapshotDir, "tmp_*.jpg"));
    }

    [Fact]
    public async Task SaveAsync_SuffixedImageFinalizeFails_KeepsExistingSnapshot()
    {
        var inspectedAt = DateTime.UtcNow;
        var (lastId, snapshotDir, _) = await SaveBaselineAsync(inspectedAt);
        var key = Guid.NewGuid();
        byte[] oldBytes = [4, 5, 6];

        for (long id = lastId + 1; id <= lastId + 20; id++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(snapshotDir, $"inspection_{id}.jpg"), oldBytes);
            Directory.CreateDirectory(
                Path.Combine(snapshotDir, $"inspection_{id}_{key:N}.jpg"));
        }

        var result = await _service.SaveAsync(
            BuildRequest(key, inspectedAt), CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Null(result.Id);
        Assert.Equal(0, await _db.Inspections.CountAsync(i => i.InspectionKey == key));
        Assert.Equal(1, await _db.Inspections.CountAsync());
        Assert.Equal(3, await _db.InspectionItems.CountAsync());
        Assert.Empty(Directory.GetFiles(snapshotDir, "tmp_*.jpg"));

        for (long id = lastId + 1; id <= lastId + 20; id++)
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(
                Path.Combine(snapshotDir, $"inspection_{id}.jpg")));
    }
}
