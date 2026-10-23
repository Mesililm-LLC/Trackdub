using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Contracts.Diagnostics;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class InferenceRetryPolicyTests
{
    [Theory]
    [InlineData("[ErrorCode:RuntimeException] DXGI_ERROR_DEVICE_REMOVED")]
    [InlineData("[ErrorCode:RuntimeException] CUDA driver reported device lost")]
    [InlineData("[ErrorCode:RuntimeException] GPU device hung")]
    [InlineData("[ErrorCode:Fail] CUDA failure 700: an illegal memory access was encountered")]
    [InlineData("[ErrorCode:EPFail] unspecified launch failure")]
    public void Device_failures_are_not_retried(string message)
    {
        // A removed device or a sticky CUDA error can never be cleared by re-running the
        // same session, so retrying only delays the failure.
        Assert.False(InferenceRetryPolicy.IsTransientMessage(message));
    }

    [Theory]
    [InlineData("[ErrorCode:RuntimeException] CUDA error: out of memory", ExecutionProviderKind.TensorRTRtx)]
    [InlineData("[ErrorCode:RuntimeException] D3D12 allocation failed", ExecutionProviderKind.DirectMl)]
    [InlineData("[ErrorCode:RuntimeException] insufficient memory for allocation", null)]
    public void Memory_exhaustion_is_retried_for_every_provider(string message, ExecutionProviderKind? provider)
    {
        // No inference-time handler re-plans onto another device, so memory pressure keeps its
        // retries; it can clear as concurrent work frees memory.
        Assert.True(InferenceRetryPolicy.IsTransientMessage(message, provider));
    }

    [Theory]
    [InlineData("[ErrorCode:RuntimeException] transient scheduling failure")]
    [InlineData("[ErrorCode:Fail] generic execution failure")]
    public void Non_device_runtime_failures_are_retried_when_provider_is_unknown(string message)
    {
        // No provider hint preserves the pre-existing permissive behavior for callers that
        // haven't been updated to pass one.
        Assert.True(InferenceRetryPolicy.IsTransientMessage(message));
    }

    [Fact]
    public void Unclassified_runtime_exception_is_retried_on_dml()
    {
        // A DML RuntimeException with no OOM/device-removed markers is typically a momentary
        // scheduling hiccup that a bare re-run against the same session can clear.
        Assert.True(InferenceRetryPolicy.IsTransientMessage(
            "[ErrorCode:RuntimeException] transient scheduling failure",
            ExecutionProviderKind.DirectMl));
    }

    [Fact]
    public void Unclassified_runtime_exception_propagates_immediately_on_tensorrt_rtx()
    {
        // TensorRT-RTX (CUDA-backed) can leave a sticky execution-context error that survives a
        // bare re-run on the same session, so this must NOT be retried: it should propagate on
        // the first throw so the device-fallback path can rebuild the session elsewhere.
        Assert.False(InferenceRetryPolicy.IsTransientMessage(
            "[ErrorCode:RuntimeException] transient scheduling failure",
            ExecutionProviderKind.TensorRTRtx));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExecutionProviderKind.Cpu)]
    [InlineData(ExecutionProviderKind.DirectMl)]
    [InlineData(ExecutionProviderKind.TensorRTRtx)]
    public void Fail_error_code_is_retried_for_every_provider(ExecutionProviderKind? provider)
    {
        Assert.True(InferenceRetryPolicy.IsTransientMessage(
            "[ErrorCode:Fail] generic execution failure", provider));
    }

    [Theory]
    [InlineData("[ErrorCode:InvalidArgument] input shape mismatch")]
    [InlineData("[ErrorCode:InvalidGraph] node has no implementation")]
    [InlineData("[ErrorCode:NotImplemented] op is unsupported")]
    public void Permanent_failures_are_not_retried(string message)
    {
        Assert.False(InferenceRetryPolicy.IsTransientMessage(message));
    }

    [Theory]
    [InlineData(ExecutionProviderKind.TensorRTRtx)]
    [InlineData(ExecutionProviderKind.DirectMl)]
    [InlineData(ExecutionProviderKind.Cpu)]
    public void Permanent_failures_are_not_retried_regardless_of_provider(ExecutionProviderKind provider)
    {
        Assert.False(InferenceRetryPolicy.IsTransientMessage(
            "[ErrorCode:InvalidArgument] input shape mismatch", provider));
    }

    [Fact]
    public void Cancellable_run_returns_every_output()
    {
        using var session = new InferenceSession(IdentityOnnxModel);
        using var cts = new CancellationTokenSource();

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            session.RunWithRetry([IdentityInput(3f)], cancellationToken: cts.Token);

        Assert.Equal("y", Assert.Single(outputs).Name);
        Assert.Equal(3f, outputs.First().AsTensor<float>()[0]);
    }

    [Fact]
    public void Cancelled_token_stops_before_native_run()
    {
        using var session = new InferenceSession(IdentityOnnxModel);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => session.RunWithRetry([IdentityInput(1f)], cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Async_run_honors_cancellation()
    {
        using var session = new InferenceSession(IdentityOnnxModel);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => session.RunWithRetryAsync([IdentityInput(1f)], cancellationToken: cts.Token));
    }

    [Fact]
    public void Binding_run_leaves_caller_run_options_reusable()
    {
        using var session = new InferenceSession(IdentityOnnxModel);
        using var runOptions = new RunOptions();
        using OrtIoBinding binding = session.CreateIoBinding();
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(new[] { 2f }, new long[] { 1 });
        binding.BindInput("x", input);
        binding.BindOutputToDevice("y", OrtMemoryInfo.DefaultInstance);
        using var cts = new CancellationTokenSource();

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            session.RunWithBindingAndNamesRetry(runOptions, binding, ["y"], cancellationToken: cts.Token);

        Assert.Equal(2f, outputs.First().AsTensor<float>()[0]);
        Assert.False(runOptions.Terminate);
    }

    [Fact]
    public void Run_completed_after_cancellation_throws_and_disposes_outputs()
    {
        // ORT may complete a run before observing Terminate; the result of a run that was
        // cancelled mid-flight must never reach the caller as a success.
        using var runOptions = new RunOptions();
        using var cts = new CancellationTokenSource();
        var outputs = new FakeOutputs();

        Assert.Throws<OperationCanceledException>(() => InferenceRetryPolicy.RunOnce(
            () => { cts.Cancel(); return outputs; }, runOptions, cts.Token));

        Assert.Equal(1, outputs.DisposeCount);
        Assert.False(runOptions.Terminate);
    }

    [Fact]
    public void Run_failing_after_cancellation_surfaces_as_operation_canceled()
    {
        // Cancel runs the registered Terminate callback synchronously, so the run starts
        // pre-terminated and ORT fails it with [ErrorCode:Fail]; the policy must surface
        // that as OperationCanceledException, never as a retryable OnnxRuntimeException.
        using var session = new InferenceSession(IdentityOnnxModel);
        using var runOptions = new RunOptions();
        using var cts = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => InferenceRetryPolicy.RunOnce(
            () => { cts.Cancel(); return session.Run([IdentityInput(1f)], session.OutputNames, runOptions); },
            runOptions, cts.Token));

        Assert.False(runOptions.Terminate);
    }

    [Fact]
    public void Run_without_cancellation_returns_outputs_undisposed()
    {
        using var runOptions = new RunOptions();
        using var cts = new CancellationTokenSource();
        var outputs = new FakeOutputs();

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> returned = InferenceRetryPolicy.RunOnce(
            () => outputs, runOptions, cts.Token);

        Assert.Same(outputs, returned);
        Assert.Equal(0, outputs.DisposeCount);
    }

    [Fact]
    public void Pre_terminated_binding_run_fails_without_releasing_a_success()
    {
        // A run against a caller-terminated RunOptions fails with [ErrorCode:Fail], which is
        // transient; a retry with the flag reset would succeed and hand back outputs the
        // caller explicitly asked to terminate. The caller's Terminate value must survive.
        using var session = new InferenceSession(IdentityOnnxModel);
        using var runOptions = new RunOptions { Terminate = true };
        using OrtIoBinding binding = session.CreateIoBinding();
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(new[] { 2f }, new long[] { 1 });
        binding.BindInput("x", input);
        binding.BindOutputToDevice("y", OrtMemoryInfo.DefaultInstance);
        using var cts = new CancellationTokenSource();

        Assert.Throws<OnnxRuntimeException>(() => session.RunWithBindingAndNamesRetry(
            runOptions, binding, ["y"], cancellationToken: cts.Token));

        Assert.True(runOptions.Terminate);
    }

    [Fact]
    public void Completed_run_reports_cold_start_evidence_with_stage_attribution()
    {
        // The run retry wrapper is the completion choke point for standard ORT engine
        // inference. Under a stage phase (the context RunStageWorkflowAsync installs), the
        // first completed run must land in the stage milestone with the stage name.
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        using (ColdStartCapture.EnterPhase(ColdStartPhase.Stage, "asr"))
        using (var session = new InferenceSession(IdentityOnnxModel))
        {
            using var outputs = session.RunWithRetry([IdentityInput(1f)], cancellationToken: CancellationToken.None);
        }

        PipelineColdStartTiming timing = capture.BuildTiming();
        Assert.Equal(InferenceRetryPolicy.Source, timing.FirstInferenceSource);
        Assert.Equal(ColdStartPhase.Stage, timing.FirstInferencePhase);
        Assert.Equal("asr", timing.FirstInferenceStageName);
        Assert.NotNull(timing.FirstStageInferenceCompletionElapsedMs);
        Assert.True(timing.FirstStageInferenceCompletionElapsedMs >= 0);
    }

    [Fact]
    public void RunOnce_records_the_first_completion_only()
    {
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        using (ColdStartCapture.EnterPhase(ColdStartPhase.Stage, "asr"))
        {
            using var first = InferenceRetryPolicy.RunOnce(
                () => new FakeOutputs(), runOptions: null, CancellationToken.None);
            using var second = InferenceRetryPolicy.RunOnce(
                () => new FakeOutputs(), runOptions: null, CancellationToken.None);
        }

        Assert.Equal(InferenceRetryPolicy.Source, capture.BuildTiming().FirstInferenceSource);
    }

    [Fact]
    public void Failed_run_reports_no_cold_start_evidence()
    {
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        {
            Assert.Throws<InvalidOperationException>(() => InferenceRetryPolicy.RunOnce(
                static () => throw new InvalidOperationException("boom"),
                runOptions: null,
                CancellationToken.None));
        }

        Assert.Null(capture.BuildTiming().FirstInferenceCompletionElapsedMs);
    }

    [Fact]
    public void Cancelled_run_reports_no_cold_start_evidence()
    {
        var capture = new ColdStartCapture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using (ColdStartCapture.Activate(capture))
        {
            Assert.Throws<OperationCanceledException>(() => InferenceRetryPolicy.RunOnce(
                () => new FakeOutputs(), runOptions: null, cts.Token));
        }

        Assert.Null(capture.BuildTiming().FirstInferenceCompletionElapsedMs);
    }

    [Fact]
    public void RunOnce_without_active_capture_is_a_no_op()
    {
        // The common case (benchmarks, tests, ad-hoc runs): marks must never throw and
        // never require a capture.
        using var outputs = InferenceRetryPolicy.RunOnce(
            () => new FakeOutputs(), runOptions: null, CancellationToken.None);
    }

    private sealed class FakeOutputs : IDisposableReadOnlyCollection<DisposableNamedOnnxValue>
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;

        public int Count => 0;

        public DisposableNamedOnnxValue this[int index] =>
            throw new ArgumentOutOfRangeException(nameof(index), "FakeOutputs is empty.");

        public IEnumerator<DisposableNamedOnnxValue> GetEnumerator() =>
            Enumerable.Empty<DisposableNamedOnnxValue>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static NamedOnnxValue IdentityInput(float value) =>
        NamedOnnxValue.CreateFromTensor("x", new DenseTensor<float>(new[] { value }, [1]));

    // x (float32 [1]) -> Identity -> y (float32 [1]); ir_version 7, opset 9.
    private static readonly byte[] IdentityOnnxModel =
    [
        0x08, 0x07, 0x3A, 0x3A, 0x0A, 0x10, 0x0A, 0x01, 0x78, 0x12, 0x01, 0x79, 0x22, 0x08,
        0x49, 0x64, 0x65, 0x6E, 0x74, 0x69, 0x74, 0x79, 0x12, 0x04, 0x74, 0x65, 0x73, 0x74,
        0x5A, 0x0F, 0x0A, 0x01, 0x78, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01, 0x12, 0x04, 0x0A,
        0x02, 0x08, 0x01, 0x62, 0x0F, 0x0A, 0x01, 0x79, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01,
        0x12, 0x04, 0x0A, 0x02, 0x08, 0x01, 0x42, 0x04, 0x0A, 0x00, 0x10, 0x09,
    ];
}
