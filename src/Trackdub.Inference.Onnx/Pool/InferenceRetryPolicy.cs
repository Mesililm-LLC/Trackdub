using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.Diagnostics;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Provides retry-with-backoff around ONNX Runtime inference calls for transient
/// ONNX Runtime failures that re-running the <em>same</em> session can actually clear
/// (for example a momentary DirectML scheduling hiccup).
/// </summary>
/// <remarks>
/// This policy re-runs the existing session; it never recreates it, and nothing in the
/// inference path currently catches a run failure to rebuild the session on another device
/// (<c>DeviceFallbackSessionCreator</c> only covers session creation). Failures therefore
/// propagate to the caller and fail the stage. Device failures — device removed/lost/hung and
/// sticky CUDA errors, as classified by <see cref="DeviceOomExceptionHelper"/> — can never be
/// cleared by a re-run, so they are not retried. Memory exhaustion can clear as other work frees
/// memory, so it is retried like any other transient failure.
///
/// A plain "[ErrorCode:RuntimeException]" that doesn't match a known device-level
/// pattern is ambiguous: on DirectML it is usually a momentary scheduling hiccup that
/// a bare re-run clears, but on TensorRT-RTX (CUDA-backed) it can mean the execution
/// context itself is corrupted, in which case re-running the same session just
/// re-throws the same error after burning the backoff budget. Callers that know their
/// session's execution provider should pass it via <c>provider</c> so TensorRT-RTX
/// sessions propagate immediately instead of retrying.
/// </remarks>
internal static class InferenceRetryPolicy
{
    /// <summary>
    /// Choke point label recorded on cold-start evidence for every inference that completes
    /// through this retry wrapper.
    /// </summary>
    internal const string Source = "onnx-run";

    private const int DefaultMaxAttempts = 3;
    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(800)
    ];

    /// <summary>
    /// Executes an ONNX Runtime inference call with retry on transient <see cref="OnnxRuntimeException"/>.
    /// </summary>
    /// <remarks>
    /// Cancellation sets <see cref="RunOptions.Terminate"/>, which ORT honors between graph nodes
    /// (a fused EP subgraph such as a TensorRT-RTX engine finishes its current execution first).
    /// <c>Run</c> still returns before the caller can release the session lease or input buffers,
    /// and a terminated run surfaces as <see cref="OperationCanceledException"/>, never a retry.
    /// Because ORT may complete a run before observing <c>Terminate</c>, outputs returned after
    /// cancellation are disposed and surfaced as <see cref="OperationCanceledException"/> too.
    /// A caller pre-set <c>Terminate</c> is preserved, and a run against a pre-terminated
    /// <see cref="RunOptions"/> is never retried.
    /// </remarks>
    /// <param name="provider">
    /// The execution provider the session was created with, if known. Narrows retry of an
    /// unclassified "[ErrorCode:RuntimeException]" — see remarks on <see cref="InferenceRetryPolicy"/>.
    /// </param>
    /// <param name="outputNames">
    /// Outputs to fetch; every output when <see langword="null"/>. Naming only the outputs a caller
    /// reads avoids copying unused tensors back from the device.
    /// </param>
    public static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunWithRetry(
        this InferenceSession session,
        IReadOnlyCollection<NamedOnnxValue> inputs,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default,
        ExecutionProviderKind? provider = null,
        IReadOnlyCollection<string>? outputNames = null)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return Execute(
                () =>
                {
                    using IDisposable? permit = CpuExecutionAdmission.Shared.Acquire(session, provider, cancellationToken);
                    return outputNames is null ? session.Run(inputs) : session.Run(inputs, outputNames);
                },
                maxAttempts,
                cancellationToken,
                provider,
                session: session);
        }

        using var runOptions = new RunOptions();
        return Execute(
            () =>
            {
                using IDisposable? permit = CpuExecutionAdmission.Shared.Acquire(session, provider, cancellationToken);
                return session.Run(inputs, outputNames ?? session.OutputNames, runOptions);
            },
            maxAttempts,
            cancellationToken,
            provider,
            runOptions,
            session);
    }

    /// <summary>
    /// Async variant that yields during backoff delays.
    /// </summary>
    /// <param name="provider">
    /// The execution provider the session was created with, if known. Narrows retry of an
    /// unclassified "[ErrorCode:RuntimeException]" — see remarks on <see cref="InferenceRetryPolicy"/>.
    /// </param>
    public static async Task<IDisposableReadOnlyCollection<DisposableNamedOnnxValue>> RunWithRetryAsync(
        this InferenceSession session,
        IReadOnlyCollection<NamedOnnxValue> inputs,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default,
        ExecutionProviderKind? provider = null)
    {
        using var runOptions = new RunOptions();
        int attempt = 0;
        while (true)
        {
            try
            {
                using IDisposable? permit = await CpuExecutionAdmission.Shared
                    .AcquireAsync(session, provider, cancellationToken)
                    .ConfigureAwait(false);
                return RunOnce(() => session.Run(inputs, session.OutputNames, runOptions), runOptions, cancellationToken);
            }
            catch (OnnxRuntimeException ex) when (!cancellationToken.IsCancellationRequested &&
                                                  !runOptions.Terminate &&
                                                  IsTransient(ex, provider, session) && ++attempt < maxAttempts)
            {
                await Task.Delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// IoBinding variant of <see cref="RunWithRetry"/>, for sessions invoked via
    /// <see cref="InferenceSession.RunWithBindingAndNames"/> instead of plain
    /// <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>. A retry re-runs the
    /// same <paramref name="binding"/> unchanged, so it is safe only when the caller has not yet
    /// consumed/swapped any bound output buffers for this attempt — the same input/output pointers
    /// are simply replayed.
    /// </summary>
    /// <param name="provider">
    /// The execution provider the session was created with, if known. Narrows retry of an
    /// unclassified "[ErrorCode:RuntimeException]" — see remarks on <see cref="InferenceRetryPolicy"/>.
    /// </param>
    public static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunWithBindingAndNamesRetry(
        this InferenceSession session,
        RunOptions runOptions,
        OrtIoBinding binding,
        string[] outputNames,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default,
        ExecutionProviderKind? provider = null) =>
        Execute(
            () =>
            {
                using IDisposable? permit = CpuExecutionAdmission.Shared.Acquire(session, provider, cancellationToken);
                return session.RunWithBindingAndNames(runOptions, binding, outputNames);
            },
            maxAttempts,
            cancellationToken,
            provider,
            runOptions,
            session);

    internal static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Execute(
        Func<IDisposableReadOnlyCollection<DisposableNamedOnnxValue>> run,
        int maxAttempts,
        CancellationToken cancellationToken,
        ExecutionProviderKind? provider,
        RunOptions? runOptions = null,
        InferenceSession? session = null)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return RunOnce(run, runOptions, cancellationToken);
            }
            catch (OnnxRuntimeException ex) when (!cancellationToken.IsCancellationRequested &&
                                                  !(runOptions?.Terminate ?? false) &&
                                                  IsTransient(ex, provider, session) && ++attempt < maxAttempts)
            {
                cancellationToken.WaitHandle.WaitOne(BackoffFor(attempt));
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    internal static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunOnce(
        Func<IDisposableReadOnlyCollection<DisposableNamedOnnxValue>> run,
        RunOptions? runOptions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var inference = BenchmarkPhaseCapture.Start("onnx-inference");
        if (runOptions is null || !cancellationToken.CanBeCanceled)
        {
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> completed = run();
            ColdStartCapture.MarkInferenceCompleted(Source);
            return completed;
        }

        // Capture the caller's initial Terminate value and restore it after the run, so a
        // caller-owned RunOptions stays reusable exactly as the caller left it. The retry
        // filters run BEFORE this finally (CLR two-pass unwinding: filters evaluate in pass 1,
        // finally blocks in pass 2) and observe the pre-restore flag: a caller-pre-set
        // Terminate blocks the retry directly, and a cancellation-set flag co-occurs with
        // IsCancellationRequested, which the filters' first clause already excludes. The
        // restore serves caller reuse and the next attempt.
        bool initialTerminate = runOptions.Terminate;
        try
        {
            // Disposing the registration waits for an in-flight callback, so a late callback
            // cannot set Terminate after the restore in the outer finally below.
            using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                static state => ((RunOptions)state!).Terminate = true, runOptions);
            try
            {
                IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = run();
                // Cold-start evidence: the run returned, so this is a completed inference.
                ColdStartCapture.MarkInferenceCompleted(Source);
                // ORT honors Terminate between graph nodes and may complete a run that was
                // cancelled mid-flight; never surface results produced after cancellation.
                if (cancellationToken.IsCancellationRequested)
                {
                    outputs.Dispose();
                    throw new OperationCanceledException(cancellationToken);
                }

                return outputs;
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        finally
        {
            runOptions.Terminate = initialTerminate;
        }
    }

    private static TimeSpan BackoffFor(int attempt) =>
        DefaultDelays[Math.Min(attempt - 1, DefaultDelays.Length - 1)];

    private static bool IsTransient(OnnxRuntimeException ex, ExecutionProviderKind? provider, InferenceSession? session)
    {
        // Callers often omit the provider hint, so a session tracked as TensorRT-RTX also counts:
        // an unpoisoned OOM would let the session's later disposal hit the native teardown crash.
        if (provider is ExecutionProviderKind.TensorRTRtx
            || (session is not null && TensorRtRtxTeardownGuard.IsTracked(session)))
        {
            TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(ex);
        }

        return IsTransientMessage(ex.Message, provider);
    }

    internal static bool IsTransientMessage(string message, ExecutionProviderKind? provider = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        switch (DeviceOomExceptionHelper.ClassifyDeviceExceptionMessage(message))
        {
            // Device removed/lost/hung, or a sticky CUDA error (illegal address, launch failure,
            // ...) that leaves the process's CUDA state unusable: a re-run can never succeed.
            case DeviceDegradationKind.DeviceFailed:
                return false;

            // Memory pressure can clear once concurrent work releases its allocations, and a CUDA
            // out-of-memory error is not sticky. Nothing re-plans inference-time failures onto
            // another device yet, so dropping these retries would only fail the stage sooner.
            case DeviceDegradationKind.MemoryExhausted:
                return true;
        }

        // [ErrorCode:Fail] is a generic execution failure with no device-level cause; a bare
        // re-run can clear it regardless of execution provider.
        if (message.Contains("[ErrorCode:Fail]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (message.Contains("[ErrorCode:RuntimeException]", StringComparison.OrdinalIgnoreCase))
        {
            // Unclassified (non-OOM, non-device-failure) RuntimeException. On TensorRT-RTX this
            // can be a CUDA execution-context error that survives a bare re-run on the same
            // session, so propagate it to the caller immediately instead of burning the backoff
            // budget on a doomed retry.
            return provider != ExecutionProviderKind.TensorRTRtx;
        }

        // Permanent codes (InvalidArgument, InvalidGraph, NotImplemented, etc.) are not retried.
        return false;
    }
}
