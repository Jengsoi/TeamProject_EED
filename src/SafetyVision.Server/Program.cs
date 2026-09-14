using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SafetyVision.Core.Configuration;
using SafetyVision.Data;
using SafetyVision.Data.Seeding;
using SafetyVision.Data.Services;
using SafetyVision.Server.Inference;
using SafetyVision.Server.Networking;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

var options = new SafetyVisionOptions();
builder.Configuration.Bind(options);

var validationErrors = options.Validate().ToList();
if (validationErrors.Count > 0)
{
    foreach (var e in validationErrors) Console.Error.WriteLine($"[설정 오류] {e}");
    return 1;
}

builder.Services.AddSingleton(options);
builder.Services.AddDbContext<SafetyVisionDbContext>(o =>
    o.UseMySql(options.ConnectionStrings.MySql, ServerVersion.AutoDetect(options.ConnectionStrings.MySql)));

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DashboardQueryService>();
builder.Services.AddScoped<HistoryQueryService>();
builder.Services.AddScoped<InspectionSaveService>();

if (options.UseFakeDetection)
    builder.Services.AddSingleton<IPpeDetector, FakePpeDetector>();
else
    builder.Services.AddSingleton<IPpeDetector, OnnxPpeDetector>();

builder.Services.AddSingleton<TcpServerHost>();

using var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var tcpHost = app.Services.GetRequiredService<TcpServerHost>();
var detector = app.Services.GetRequiredService<IPpeDetector>();

if (!detector.IsAvailable)
    logger.LogWarning("PPE 검출기를 사용할 수 없습니다: {Reason}", detector.UnavailableReason);
else if (detector.IsFakeMode)
    logger.LogWarning("UseFakeDetection=true: 개발용 Fake 판정을 사용합니다. 최종 발표본에서는 false여야 합니다.");
else
    logger.LogInformation("PPE 검출기 준비 완료 (모델: {Name} {Version}, 경로: {Path})", options.ModelName, options.ModelVersion, options.ModelPath);

try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SafetyVisionDbContext>();
    await db.Database.MigrateAsync();
    await AdminSeeder.EnsureSeedAdminAsync(db);
    tcpHost.DatabaseReady = true;
    logger.LogInformation("MySQL 연결 및 마이그레이션 완료.");
}
catch (Exception ex)
{
    logger.LogError(ex, "MySQL 연결/마이그레이션 실패. 신규 클라이언트 연결을 거부합니다.");
    tcpHost.DatabaseReady = false;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

await tcpHost.RunAsync(cts.Token);
return 0;
