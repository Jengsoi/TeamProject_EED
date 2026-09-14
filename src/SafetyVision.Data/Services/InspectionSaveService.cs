using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SafetyVision.Core.Analysis;
using SafetyVision.Core.Domain;
using SafetyVision.Data.Entities;

namespace SafetyVision.Data.Services;

public sealed record SaveInspectionRequest(
    Guid InspectionKey,
    DateTime InspectedAtUtc,
    InspectionResultType Result,
    IReadOnlyList<EquipmentJudgement> Items,
    byte[]? RepresentativeJpeg,
    double? PersonConfidence,
    string CameraName,
    string ModelName,
    string ModelVersion);

public enum SaveOutcome { Success, Failed }

public sealed record SaveInspectionResult(SaveOutcome Outcome, long? Id);

// 04_DB설계.md 7절: DB(MySQL 트랜잭션)와 이미지 파일이 모두 성공해야 저장 완료. 실패 시 롤백·파일 정리.
public sealed class InspectionSaveService(SafetyVisionDbContext db, ILogger<InspectionSaveService> logger)
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    public async Task<SaveInspectionResult> SaveAsync(SaveInspectionRequest request, CancellationToken ct)
    {
        await SaveGate.WaitAsync(ct);
        try
        {
            var existing = await db.Inspections.FirstOrDefaultAsync(i => i.InspectionKey == request.InspectionKey, ct);
            if (existing is not null) return new SaveInspectionResult(SaveOutcome.Success, existing.Id);

            string? tempFile = null;
            string? finalFile = null;
            bool finalFileMoved = false;

            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dateDir = request.InspectedAtUtc.ToLocalTime().ToString("yyyyMMdd");
                string snapshotDir = Path.Combine(root, "SafetyVision", "snapshots", dateDir);
                Directory.CreateDirectory(snapshotDir);
                tempFile = Path.Combine(snapshotDir, $"tmp_{Guid.NewGuid():N}.jpg");

                if (request.RepresentativeJpeg is { Length: > 0 })
                    await File.WriteAllBytesAsync(tempFile, request.RepresentativeJpeg, ct);

                await using var tx = await db.Database.BeginTransactionAsync(ct);

                var inspection = new Inspection
                {
                    InspectionKey = request.InspectionKey,
                    InspectedAt = request.InspectedAtUtc,
                    Result = request.Result.ToDbCode(),
                    PersonConfidence = request.PersonConfidence,
                    ImagePath = "",
                    CameraName = request.CameraName,
                    ModelName = request.ModelName,
                    ModelVersion = request.ModelVersion,
                    CreatedAt = DateTime.UtcNow,
                };
                foreach (var item in request.Items)
                {
                    inspection.Items.Add(new InspectionItem
                    {
                        EquipmentCode = EquipmentClassMap.ToDbCode(item.Code),
                        Status = item.Status.ToDbCode(),
                        Score = item.Score,
                        PositiveFrames = item.Positive,
                        NegativeFrames = item.Negative,
                        TotalFrames = item.Total,
                        CreatedAt = DateTime.UtcNow,
                    });
                }

                db.Inspections.Add(inspection);
                await db.SaveChangesAsync(ct);

                string finalFileName = $"inspection_{inspection.Id}.jpg";
                string finalRelativePath = Path.Combine("snapshots", dateDir, finalFileName);
                bool hasImage = File.Exists(tempFile);
                finalFile = Path.Combine(snapshotDir, finalFileName);
                inspection.ImagePath = hasImage ? finalRelativePath : "";
                await db.SaveChangesAsync(ct);

                if (hasImage)
                {
                    File.Move(tempFile, finalFile, overwrite: false);
                    finalFileMoved = true;
                }

                await tx.CommitAsync(ct);
                return new SaveInspectionResult(SaveOutcome.Success, inspection.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "검사 결과 저장 실패 (InspectionKey={Key})", request.InspectionKey);
                if (finalFileMoved && finalFile is not null && File.Exists(finalFile))
                {
                    try { File.Delete(finalFile); } catch (IOException) { /* best effort */ }
                }
                if (tempFile is not null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch (IOException) { /* best effort */ }
                }
                return new SaveInspectionResult(SaveOutcome.Failed, null);
            }
        }
        finally
        {
            SaveGate.Release();
        }
    }

    public string ResolveImageFullPath(string relativePath) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafetyVision", relativePath);
}
