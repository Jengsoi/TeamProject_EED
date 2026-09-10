using Microsoft.EntityFrameworkCore;
using SafetyVision.Data;

namespace SafetyVision.Tests.Data;

// Day6 DB 통합 테스트는 실제 MySQL이 필요하다. 실행 전 SAFETYVISION_TEST_MYSQL_CONNSTR 환경변수를
// 별도 테스트 DB(예: safetyvision_test, 데모 데이터와 분리)로 설정해야 한다. README 참고.
internal static class TestDatabase
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("SAFETYVISION_TEST_MYSQL_CONNSTR")
        ?? throw new InvalidOperationException(
            "SAFETYVISION_TEST_MYSQL_CONNSTR 환경변수가 설정되어 있지 않습니다. " +
            "Day6 DB 통합 테스트를 실행하려면 별도 테스트 DB의 연결 문자열을 설정하세요 (README 7절 참고).");

    public static SafetyVisionDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SafetyVisionDbContext>()
            .UseMySql(ConnectionString, ServerVersion.AutoDetect(ConnectionString))
            .Options;
        return new SafetyVisionDbContext(options);
    }

    public static async Task ResetAsync(SafetyVisionDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM InspectionItems");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Inspections");
    }
}
