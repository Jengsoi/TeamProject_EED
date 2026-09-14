using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace SafetyVision.Server;

internal static class LocalMySqlStarter
{
    public static async Task EnsureStartedAsync(string executable, string configFile, ILogger logger)
    {
        if (await IsListeningAsync()) return;

        if (!File.Exists(executable) || !File.Exists(configFile))
        {
            logger.LogWarning("로컬 MySQL 실행 파일 또는 설정 파일을 찾을 수 없습니다. MySQL을 직접 시작해 주세요.");
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"--defaults-file=\"{configFile}\"",
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) throw new InvalidOperationException("MySQL 프로세스를 시작하지 못했습니다.");

            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (await IsListeningAsync())
                {
                    logger.LogInformation("로컬 MySQL 시작 완료 (포트 3306).");
                    return;
                }
                if (process.HasExited) break;
                await Task.Delay(500);
            }

            logger.LogWarning("로컬 MySQL을 시작했지만 포트 3306에서 응답하지 않습니다.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "로컬 MySQL 자동 시작 실패. MySQL을 직접 시작해 주세요.");
        }
    }

    private static async Task<bool> IsListeningAsync()
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", 3306).WaitAsync(TimeSpan.FromMilliseconds(500));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
