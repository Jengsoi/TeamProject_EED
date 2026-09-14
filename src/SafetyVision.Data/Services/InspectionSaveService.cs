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
public sealed class InspectionSaveService(
    SafetyVisionDbContext db,
    ILogger<InspectionSaveService> logger,
    string? dataRoot = null)
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    private readonly string _dataRoot = dataRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafetyVision");

    public async Task<SaveInspectionResult> SaveAsync(SaveInspectionRequest request, CancellationToken ct)
    {
        await SaveGate.WaitAsync(ct);
        string? tempFile = null;
        string? finalFile = null;
        bool finalFileMoved = false;
        bool committed = false;
        try
        {
            var existing = await db.Inspections.FirstOrDefaultAsync(i => i.InspectionKey == request.InspectionKey, ct);
            if (existing is not null) return new SaveInspectionResult(SaveOutcome.Success, existing.Id);

            string dateDir = request.InspectedAtUtc.ToLocalTime().ToString("yyyyMMdd");
            string snapshotDir = Path.Combine(_dataRoot, "snapshots", dateDir);
            Directory.CreateDirectory(snapshotDir);
            tempFile = Path.Combine(snapshotDir, $"tmp_{Guid.NewGuid():N}.jpg");

            try
            {
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

                bool hasImage = File.Exists(tempFile);
                string finalFileName = $"inspection_{inspection.Id}.jpg";
                if (hasImage)
                {
                    finalFile = Path.Combine(snapshotDir, finalFileName);
                    if (File.Exists(finalFile))
                    {
                        finalFileName = $"inspection_{inspection.Id}_{request.InspectionKey:N}.jpg";
                        finalFile = Path.Combine(snapshotDir, finalFileName);
                    }
                }
                string finalRelativePath = Path.Combine("snapshots", dateDir, finalFileName);
                inspection.ImagePath = hasImage ? finalRelativePath : "";
                await db.SaveChangesAsync(ct);

                if (hasImage)
                {
                    File.Move(tempFile, finalFile!, overwrite: false);
                    finalFileMoved = true;
                }

                await tx.CommitAsync(ct);
                committed = true;
                return new SaveInspectionResult(SaveOutcome.Success, inspection.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "검사 결과 저장 실패 (InspectionKey={Key})", request.InspectionKey);
                return new SaveInspectionResult(SaveOutcome.Failed, null);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "검사 결과 저장 준비 실패 (InspectionKey={Key})", request.InspectionKey);
            return new SaveInspectionResult(SaveOutcome.Failed, null);
        }
        finally
        {
            DeleteBestEffort(tempFile);
            // 정상 커밋 후에는 DB가 이 파일을 참조하므로 보존한다. 실패 반환 경로에서만 finalFile이 남는다.
            if (!committed && finalFileMoved)
                DeleteBestEffort(finalFile);
            SaveGate.Release();
        }
    }

    private static void DeleteBestEffort(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { File.Delete(path); } catch (IOException) { /* 다음 정리 기회까지 보존 */ }
    }

    public string ResolveImageFullPath(string relativePath) =>
        Path.Combine(_dataRoot, relativePath);
}
