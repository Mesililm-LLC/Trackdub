namespace Trackdub.CloudJobAdapter;

/// <summary>Independent of AWS requests: a hung renewal cannot extend execution authority.</summary>
public sealed class ExecutionLeaseWatchdog(TimeProvider clock)
{
    private long _renewedAt = clock.GetTimestamp();
    public void Renewed() => Interlocked.Exchange(ref _renewedAt, clock.GetTimestamp());
    public void Check(CancellationTokenSource lifetime)
    {
        if (clock.GetElapsedTime(Interlocked.Read(ref _renewedAt)) >= TimeSpan.FromSeconds(60)) lifetime.Cancel();
    }
    public async Task RunAsync(CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, lifetime.Token).ConfigureAwait(false);
                Check(lifetime);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
    }
}
