namespace Trackdub.Inference.Onnx.Qwen3Tts.Models;

/// <summary>
/// Engine-lifetime pooled session holder for one Qwen3-TTS graph. The residency pin keeps
/// the session alive in <c>InferenceSessionPool</c> between syntheses; the execution lease is
/// acquired per synthesis (or per call) and released afterwards, so a second synthesis — or a
/// concurrent same-key acquire from another stage — is never starved by a gate held for the
/// engine lifetime. Disposing this holder releases the residency pin — it does not dispose
/// the pooled session itself.
/// </summary>
internal sealed class QwenPooledSession : IDisposable
{
    private readonly OnnxExecutionSessionFactory.PooledSingleSessionPin pin;
    private readonly object gate = new();
    private int disposed;

    internal QwenPooledSession(OnnxExecutionSessionFactory.PooledSingleSessionPin pin)
    {
        this.pin = pin ?? throw new ArgumentNullException(nameof(pin));
    }

    /// <summary>
    /// Acquires a short exclusive lease on the shared pooled session for one synthesis (or
    /// one call). The caller owns the lease and must dispose it; the residency pin is
    /// unaffected.
    /// </summary>
    public Task<OnnxExecutionSessionFactory.SingleSessionLease> AcquireAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return pin.AcquireAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        pin.Dispose();
    }
}
