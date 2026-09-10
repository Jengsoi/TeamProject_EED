using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SafetyVision.Data;

// `dotnet ef migrations add` 실행용 설계 시점 팩토리. 실제 연결 문자열은 서버 appsettings.json에서 온다.
public sealed class SafetyVisionDbContextFactory : IDesignTimeDbContextFactory<SafetyVisionDbContext>
{
    public SafetyVisionDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SafetyVisionDbContext>();
        string designTimeConnectionString = Environment.GetEnvironmentVariable("SAFETYVISION_MYSQL_CONNSTR")
            ?? "Server=localhost;Port=3306;Database=safetyvision;User=safetyvision_app;Password=CHANGE_ME;";
        optionsBuilder.UseMySql(designTimeConnectionString, new MySqlServerVersion(new Version(8, 0, 36)));
        return new SafetyVisionDbContext(optionsBuilder.Options);
    }
}
