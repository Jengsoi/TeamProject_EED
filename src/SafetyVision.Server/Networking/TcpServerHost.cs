using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SafetyVision.Core.Configuration;
using SafetyVision.Server.Inference;

namespace SafetyVision.Server.Networking;

// 07_통신프로토콜.md 1절: 다중 클라이언트 접속 지원, 연결마다 독립 세션.
public sealed class TcpServerHost(
    SafetyVisionOptions options,
    IPpeDetector detector,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    ILogger<TcpServerHost> logger)
{
    public bool DatabaseReady { get; set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, options.ListenPort);
        listener.Start();
        logger.LogInformation("TCP 서버 시작: 포트 {Port}", options.ListenPort);

        var sessionTasks = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcpClient;
                try
                {
                    tcpClient = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!DatabaseReady)
                {
                    logger.LogWarning("MySQL 연결 불가로 신규 클라이언트 연결을 거부합니다.");
                    tcpClient.Close();
                    continue;
                }

                var sessionLogger = loggerFactory.CreateLogger<ClientSession>();
                var session = new ClientSession(tcpClient, options, detector, scopeFactory, sessionLogger);
                sessionTasks.Add(Task.Run(() => session.RunAsync(ct), ct));
                sessionTasks.RemoveAll(t => t.IsCompleted);
            }
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(sessionTasks).ConfigureAwait(false);
        }
    }
}
