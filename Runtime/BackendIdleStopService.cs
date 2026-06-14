namespace CodebaseMemory.Wrapper.Runtime;

/// <summary>
/// Periodically stops the lazy child process after it has been idle long enough.
/// </summary>
public sealed class BackendIdleStopService(CodebaseMemoryBackend backend) : BackgroundService
{
    /// <summary>
    /// Runs a lightweight idle cleanup loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        try {
            while (await timer.WaitForNextTickAsync(stoppingToken)) {
                await backend.StopIfIdleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal host shutdown path.
        }
    }
}
