namespace SafetyVision.Client.Networking;

internal sealed class ConnectionRecoveryService(ServerConnection connection)
{
    private int _running;

    public bool TryStart() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    public async Task<bool> ReconnectAsync(CancellationToken ct = default)
    {
        try
        {
            return await connection
                .ConnectWithRetryAsync(5, TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
