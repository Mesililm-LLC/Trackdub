using System.Runtime.CompilerServices;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

/// <summary>
/// Decides when native teardown of a TensorRT-RTX <see cref="InferenceSession"/> must be skipped.
/// </summary>
/// <remarks>
/// <para>
/// Once a CUDA out-of-memory failure has been observed while building or loading a TensorRT-RTX
/// engine, the execution provider's native state is poisoned process-wide, and disposing a
/// TensorRT-RTX session can then fault with an uncatchable access violation
/// (<c>0xC0000005</c> in <c>ScopedCudaStream</c>/<c>MyelinGraphContext</c>). Skipping teardown
/// leaks that session's native memory, so it is done only while the runtime is known to be
/// poisoned, and only for sessions that really run on TensorRT-RTX. Healthy sessions — and
/// DirectML/CPU fallback sessions created under a TensorRT-RTX pool key — always dispose
/// normally, so eviction and shutdown keep releasing VRAM.
/// </para>
/// </remarks>
internal static class TensorRtRtxTeardownGuard
{
    private static readonly ConditionalWeakTable<InferenceSession, object> TrackedSessions = new();
    private static readonly object Marker = new();
    private static int poisoned;

    /// <summary>Whether a native CUDA failure has poisoned the TensorRT-RTX runtime in this process.</summary>
    internal static bool IsPoisoned => Volatile.Read(ref poisoned) != 0;

    /// <summary>Records a session whose effective execution provider is TensorRT-RTX.</summary>
    internal static void Track(InferenceSession session) => TrackedSessions.TryAdd(session, Marker);

    /// <summary>Drops a session that was tracked before effective-provider detection placed it elsewhere.</summary>
    internal static void Untrack(InferenceSession session) => TrackedSessions.Remove(session);

    /// <summary><see langword="true"/> when <paramref name="session"/> is currently tracked as a TensorRT-RTX session.</summary>
    internal static bool IsTracked(InferenceSession session) => TrackedSessions.TryGetValue(session, out _);

    /// <summary>
    /// Keeps tracking only when <paramref name="effectiveProvider"/> is TensorRT-RTX.
    /// A CPU or DirectML fallback must still release its native session after a later OOM.
    /// </summary>
    internal static void ReconcileTracking(InferenceSession session, ExecutionProviderKind effectiveProvider)
    {
        if (effectiveProvider is not ExecutionProviderKind.TensorRTRtx)
        {
            Untrack(session);
        }
    }

    /// <summary>
    /// Marks the runtime poisoned when <paramref name="failure"/> is a CUDA allocation failure.
    /// </summary>
    /// <returns><see langword="true"/> when the failure poisoned the runtime.</returns>
    internal static bool MarkPoisonedIfCudaOutOfMemory(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            string message = current.Message;
            if (message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                || message.Contains("cudaErrorMemoryAllocation", StringComparison.OrdinalIgnoreCase)
                || message.Contains("CUDA_ERROR_OUT_OF_MEMORY", StringComparison.OrdinalIgnoreCase)
                || ContainsCudaFailureCode2(message))
            {
                Volatile.Write(ref poisoned, 1);
                return true;
            }
        }

        return false;
    }

    // "CUDA failure 2" must not match longer codes such as 209 (no kernel image on an unsupported
    // GPU), 201 or 222: poisoning on those would leak every tracked session's VRAM without an OOM.
    private static bool ContainsCudaFailureCode2(string message)
    {
        const string Prefix = "CUDA failure 2";
        int index = message.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            int next = index + Prefix.Length;
            if (next >= message.Length || !char.IsAsciiDigit(message[next]))
            {
                return true;
            }

            index = message.IndexOf(Prefix, next, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="session"/> runs on TensorRT-RTX and the runtime
    /// is poisoned, so its native teardown must be skipped instead of risking a process crash.
    /// </summary>
    internal static bool ShouldSkipNativeTeardown(InferenceSession session) =>
        IsPoisoned && TrackedSessions.TryGetValue(session, out _);

    /// <summary>Disposes <paramref name="session"/>, or abandons its native state when unsafe.</summary>
    internal static void DisposeSafely(InferenceSession? session)
    {
        if (session is null)
        {
            return;
        }

        if (ShouldSkipNativeTeardown(session))
        {
            GC.SuppressFinalize(session);
            return;
        }

        session.Dispose();
    }

    /// <summary>Clears the poisoned flag and session tracking. Test seam only.</summary>
    internal static void ResetForTests()
    {
        Volatile.Write(ref poisoned, 0);
        TrackedSessions.Clear();
    }
}
