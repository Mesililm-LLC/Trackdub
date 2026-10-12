using System.Runtime.Versioning;
using Microsoft.Windows.AI.MachineLearning;
using Trackdub.Inference.Onnx.WinMlCatalog;
using Windows.Foundation;

namespace Trackdub.Inference.Onnx.WindowsMl;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsMlEnsureReady
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public static CatalogReadyState MapReadyState(ExecutionProviderReadyState state) =>
        state switch
        {
            ExecutionProviderReadyState.NotPresent => CatalogReadyState.NotPresent,
            ExecutionProviderReadyState.NotReady => CatalogReadyState.NotReady,
            ExecutionProviderReadyState.Ready => CatalogReadyState.Ready,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported Windows ML ready state."),
        };

    public static CatalogEnsureStatus MapEnsureStatus(ExecutionProviderReadyResultState status) =>
        status switch
        {
            ExecutionProviderReadyResultState.Success => CatalogEnsureStatus.Success,
            ExecutionProviderReadyResultState.InProgress => CatalogEnsureStatus.InProgress,
            ExecutionProviderReadyResultState.Failure => CatalogEnsureStatus.Failure,
            _ => CatalogEnsureStatus.Failure,
        };

    /// <summary>
    /// Awaits preparation, then retries a few times when Windows still reports
    /// <see cref="ExecutionProviderReadyResultState.InProgress"/>. This is a bounded refresh,
    /// not a poll loop.
    /// </summary>
    /// <param name="progress">
    /// Optional progress sink normalized to <c>0.0</c>–<c>1.0</c> from the WinRT
    /// <c>IAsyncOperationWithProgress&lt;,double&gt;</c> range of <c>0</c>–<c>100</c>. The
    /// handler fires on a non-UI thread; UI consumers must marshal the callback to the
    /// dispatcher. Reports are emitted only while a <c>EnsureReadyAsync</c> await is in
    /// flight, and reset to <c>0.0</c> at the start of each attempt (including retries),
    /// so a stalled attempt cannot leave a non-zero reading on the consumer.
    /// </param>
    public static async Task<ExecutionProviderReadyResult> EnsureReadyBoundedAsync(
        ExecutionProvider provider,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        ExecutionProviderReadyResult result = await InvokeWithProgressAsync(
            provider.EnsureReadyAsync(),
            progress,
            cancellationToken).ConfigureAwait(false);
        for (int attempt = 1;
             attempt < MaxAttempts && result.Status == ExecutionProviderReadyResultState.InProgress;
             attempt++)
        {
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            result = await InvokeWithProgressAsync(
                provider.EnsureReadyAsync(),
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static Task<ExecutionProviderReadyResult> InvokeWithProgressAsync(
        IAsyncOperationWithProgress<ExecutionProviderReadyResult, double> operation,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is not null)
        {
            progress.Report(0.0);
            operation.Progress += (_, percent) => ReportPercent(progress, percent);
        }
        return operation.AsTask(cancellationToken);
    }

    /// <summary>
    /// Normalizes the WinRT 0–100 percentage to the 0.0–1.0 range that
    /// <see cref="IProgress{T}"/> consumers expect. <paramref name="progress"/> is
    /// assumed non-null; callers gate on it.
    /// </summary>
    internal static void ReportPercent(IProgress<double> progress, double rawPercent) =>
        progress.Report(rawPercent / 100.0);

    public static string FormatHResult(ExecutionProviderReadyResult result) =>
        result.ExtendedError is null ? "n/a" : $"0x{result.ExtendedError.HResult:X8}";
}
