using Microsoft.Windows.AI.MachineLearning;
using Trackdub.Inference.Onnx.WindowsMl;
using Windows.Foundation;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class WindowsMlEnsureReadyProgressTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(50.0, 0.5)]
    [InlineData(100.0, 1.0)]
    [InlineData(33.5, 0.335)]
    public void ReportPercent_normalizes_winrt_range_to_unit_interval(double rawPercent, double expected)
    {
        var sink = new RecordingProgress();
        WindowsMlEnsureReady.ReportPercent(sink, rawPercent);

        Assert.Equal(new[] { expected }, sink.Reports);
    }

    [Fact]
    public void ReportPercent_emits_zero_for_raw_zero()
    {
        var sink = new RecordingProgress();
        WindowsMlEnsureReady.ReportPercent(sink, 0.0);

        Assert.Equal(new[] { 0.0 }, sink.Reports);
    }

    [Fact]
    public void ReportPercent_emits_one_for_raw_hundred()
    {
        var sink = new RecordingProgress();
        WindowsMlEnsureReady.ReportPercent(sink, 100.0);

        Assert.Equal(new[] { 1.0 }, sink.Reports);
    }

    [Fact]
    public void ReportPercent_reports_each_call_independently()
    {
        var sink = new RecordingProgress();
        WindowsMlEnsureReady.ReportPercent(sink, 10.0);
        WindowsMlEnsureReady.ReportPercent(sink, 25.0);
        WindowsMlEnsureReady.ReportPercent(sink, 75.0);
        WindowsMlEnsureReady.ReportPercent(sink, 100.0);

        Assert.Equal(new[] { 0.10, 0.25, 0.75, 1.0 }, sink.Reports);
    }

    [Fact]
    public async Task InvokeWithProgressAsync_nullProgress_skipsReportsAndSubscription()
    {
        var operation = new FakeEnsureReadyOperation();
        var sink = new RecordingProgress();

        Task<ExecutionProviderReadyResult> invocation =
            WindowsMlEnsureReady.InvokeWithProgressAsync(operation, progress: null, CancellationToken.None);

        Assert.False(operation.ProgressHandlerAttached);
        Assert.Empty(sink.Reports);

        operation.Complete();
        await invocation;

        Assert.Empty(sink.Reports);
    }

    [Fact]
    public async Task InvokeWithProgressAsync_reportsLeadingZeroThenForwardedPercent()
    {
        var operation = new FakeEnsureReadyOperation();
        var sink = new RecordingProgress();

        Task<ExecutionProviderReadyResult> invocation =
            WindowsMlEnsureReady.InvokeWithProgressAsync(operation, sink, CancellationToken.None);

        // The leading 0.0 report lets a consumer reset its UI before the operation emits anything.
        Assert.True(operation.ProgressHandlerAttached);
        Assert.Equal(new[] { 0.0 }, sink.Reports);

        operation.FireProgress(25.0);
        operation.FireProgress(100.0);
        operation.Complete();
        await invocation;

        Assert.Equal(new[] { 0.0, 0.25, 1.0 }, sink.Reports);
    }

    [Fact]
    public async Task InvokeWithProgressAsync_reportsFreshZeroForEachAttempt()
    {
        var sink = new RecordingProgress();

        // Two sequential attempts, as EnsureReadyBoundedAsync issues for an initial call and a retry.
        var first = new FakeEnsureReadyOperation();
        Task<ExecutionProviderReadyResult> firstAttempt =
            WindowsMlEnsureReady.InvokeWithProgressAsync(first, sink, CancellationToken.None);
        first.FireProgress(40.0);
        first.Complete();
        await firstAttempt;

        var retry = new FakeEnsureReadyOperation();
        Task<ExecutionProviderReadyResult> retryAttempt =
            WindowsMlEnsureReady.InvokeWithProgressAsync(retry, sink, CancellationToken.None);
        retry.FireProgress(80.0);
        retry.Complete();
        await retryAttempt;

        // Every attempt starts with a fresh 0.0 so a stalled attempt cannot leave a non-zero
        // reading on the consumer between retries.
        Assert.Equal(new[] { 0.0, 0.4, 0.0, 0.8 }, sink.Reports);
    }

    [Fact]
    public async Task InvokeWithProgressAsync_detachesProgressHandler_whenAwaitEnds()
    {
        var operation = new FakeEnsureReadyOperation();
        var sink = new RecordingProgress();

        Task<ExecutionProviderReadyResult> invocation =
            WindowsMlEnsureReady.InvokeWithProgressAsync(operation, sink, CancellationToken.None);

        operation.Complete();
        await invocation;

        Assert.False(operation.ProgressHandlerAttached);

        // A late WinRT callback after the await ends must not reach the sink.
        operation.FireProgress(75.0);
        Assert.Equal(new[] { 0.0 }, sink.Reports);
    }

    [Fact]
    public async Task InvokeWithProgressAsync_detachesProgressHandler_whenCancelled()
    {
        var operation = new FakeEnsureReadyOperation();
        var sink = new RecordingProgress();

        using var cts = new CancellationTokenSource();
        Task<ExecutionProviderReadyResult> invocation =
            WindowsMlEnsureReady.InvokeWithProgressAsync(operation, sink, cts.Token);

        operation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);

        Assert.False(operation.ProgressHandlerAttached);

        // A late WinRT callback after a cancelled await must not reach the sink.
        operation.FireProgress(60.0);
        Assert.Equal(new[] { 0.0 }, sink.Reports);
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = new();

        public void Report(double value) => Reports.Add(value);
    }

    private sealed class FakeEnsureReadyOperation : IAsyncOperationWithProgress<ExecutionProviderReadyResult, double>
    {
        private AsyncOperationProgressHandler<ExecutionProviderReadyResult, double>? _progressHandler;
        private AsyncOperationWithProgressCompletedHandler<ExecutionProviderReadyResult, double>? _completedHandler;

        public AsyncOperationProgressHandler<ExecutionProviderReadyResult, double>? Progress
        {
            get => _progressHandler;
            set => _progressHandler = value;
        }

        public AsyncOperationWithProgressCompletedHandler<ExecutionProviderReadyResult, double>? Completed
        {
            get => _completedHandler;
            set => _completedHandler = value;
        }

        public bool ProgressHandlerAttached => _progressHandler is not null;

        public uint Id => 0;

        public AsyncStatus Status { get; private set; } = AsyncStatus.Started;

        public Exception? ErrorCode { get; private set; }

        // The wiring tests never inspect the result payload; null is sufficient for AsTask to complete.
        public ExecutionProviderReadyResult GetResults() => default!;

        public void FireProgress(double percent) => _progressHandler?.Invoke(this, percent);

        public void Complete()
        {
            Status = AsyncStatus.Completed;
            _completedHandler?.Invoke(this, AsyncStatus.Completed);
        }

        public void Cancel()
        {
            Status = AsyncStatus.Canceled;
            _completedHandler?.Invoke(this, AsyncStatus.Canceled);
        }

        public void Close()
        {
        }
    }
}
