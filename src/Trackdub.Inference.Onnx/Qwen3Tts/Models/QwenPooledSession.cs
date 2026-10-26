using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.Qwen3Tts.Models;

/// <summary>
/// Engine-lifetime pooled session for one Qwen3-TTS graph. The residency pin keeps the
/// session alive in <c>InferenceSessionPool</c> between syntheses; the execution lease is
/// acquired once and cached, so a second synthesis reuses the same
/// <see cref="InferenceSession"/> instead of disposing and recreating it. Disposing this
/// holder returns the lease to the pool and releases the residency pin — it does not
/// dispose the pooled session itself.
/// </summary>
internal sealed class QwenPooledSession : IDisposable
{
    private readonly OnnxExecutionSessionFactory.PooledSingleSessionPin pin;
    private readonly object gate = new();
    private OnnxExecutionSessionFactory.SingleSessionLease? lease;
    private int disposed;

    internal QwenPooledSession(OnnxExecutionSessionFactory.PooledSingleSessionPin pin)
    {
        this.pin = pin ?? throw new ArgumentNullException(nameof(pin));
    }

    /// <summary>The shared pooled session, acquired lazily on first use.</summary>
    public InferenceSession Session
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            lock (gate)
            {
                lease ??= pin.AcquireAsync(CancellationToken.None).GetAwaiter().GetResult();
                return lease.Session;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lock (gate)
        {
            lease?.Dispose();
            lease = null;
        }

        pin.Dispose();
    }
}
