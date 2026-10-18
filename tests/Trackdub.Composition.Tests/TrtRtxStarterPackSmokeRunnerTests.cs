using Trackdub.Composition.StarterPacks;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class TrtRtxStarterPackSmokeRunnerTests
{
    [Fact]
    public async Task RunAsync_SkipsUncachedTargetsWithoutAttemptingSmoke()
    {
        var smokeTester = new FakeExecutionProviderSmokeTester(
            (_, _) => throw new InvalidOperationException("Smoke should not run for skipped targets."));

        TrtRtxStarterPackSmokeReport report = await TrtRtxStarterPackSmokeRunner.RunAsync(
            modelCacheDirectory: Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            smokeTester,
            CancellationToken.None);

        Assert.Equal(0, report.Attempted);
        Assert.Equal(TrtRtxSmokeCatalog.StarterPackTurboGpu.Count, report.Skipped);
        Assert.False(report.HasFailures);
        Assert.All(report.Targets, target => Assert.Equal(TrtRtxStarterPackSmokeTargetStatus.Skipped, target.Status));
    }

    [Fact]
    public async Task RunAsync_RecordsFailedSmokeResults()
    {
        var smokeTester = new FakeExecutionProviderSmokeTester((request, _) =>
        {
            if (request.ExecutionProvider != ExecutionProviderKind.TensorRTRtx)
            {
                return new ExecutionProviderSmokeTestResult(false, "Expected TRT RTX provider pin.");
            }

            return new ExecutionProviderSmokeTestResult(false, "simulated failure");
        });

        TrtRtxStarterPackSmokeReport report = await TrtRtxStarterPackSmokeRunner.RunAsync(
            modelCacheDirectory: null,
            smokeTester,
            CancellationToken.None);

        if (report.Attempted == 0)
        {
            return;
        }

        Assert.True(report.Failed > 0);
        Assert.Contains(
            report.Targets,
            target => target.Status == TrtRtxStarterPackSmokeTargetStatus.Failed
                && string.Equals(target.Detail, "simulated failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_PassesRequestedProviderToSmokeTester()
    {
        string modelCacheDirectory = CreateCacheWithSingleModel();
        try
        {
            ExecutionProviderSmokeTestRequest? capturedRequest = null;
            var smokeTester = new FakeExecutionProviderSmokeTester((request, _) =>
            {
                capturedRequest = request;
                return new ExecutionProviderSmokeTestResult(true);
            });

            TrtRtxStarterPackSmokeReport report = await TrtRtxStarterPackSmokeRunner.RunAsync(
                modelCacheDirectory,
                smokeTester,
                [new TrtRtxSmokeCatalog.Target("cgus/diar_streaming_sortformer_4spk-v2.1-onnx", null, "diarization")],
                progress: null,
                CancellationToken.None,
                provider: ExecutionProviderKind.Cuda);

            Assert.Equal(1, report.Attempted);
            Assert.Equal(1, report.Passed);
            Assert.NotNull(capturedRequest);
            Assert.Equal(ExecutionProviderKind.Cuda, capturedRequest!.ExecutionProvider);
        }
        finally
        {
            Directory.Delete(modelCacheDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_DefaultsToTensorRTRtxWhenProviderOmitted()
    {
        string modelCacheDirectory = CreateCacheWithSingleModel();
        try
        {
            ExecutionProviderSmokeTestRequest? capturedRequest = null;
            var smokeTester = new FakeExecutionProviderSmokeTester((request, _) =>
            {
                capturedRequest = request;
                return new ExecutionProviderSmokeTestResult(true);
            });

            TrtRtxStarterPackSmokeReport report = await TrtRtxStarterPackSmokeRunner.RunAsync(
                modelCacheDirectory,
                smokeTester,
                [new TrtRtxSmokeCatalog.Target("cgus/diar_streaming_sortformer_4spk-v2.1-onnx", null, "diarization")],
                progress: null,
                CancellationToken.None);

            Assert.Equal(1, report.Attempted);
            Assert.NotNull(capturedRequest);
            Assert.Equal(ExecutionProviderKind.TensorRTRtx, capturedRequest!.ExecutionProvider);
        }
        finally
        {
            Directory.Delete(modelCacheDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyEntryPathAsync_PassesRequestedProviderToSmokeTester()
    {
        string entryFile = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(entryFile, [0x00]);
        try
        {
            ExecutionProviderSmokeTestRequest? capturedRequest = null;
            var smokeTester = new FakeExecutionProviderSmokeTester((request, _) =>
            {
                capturedRequest = request;
                return new ExecutionProviderSmokeTestResult(true);
            });

            TrtRtxStarterPackSmokeTargetResult result = await TrtRtxStarterPackSmokeRunner.VerifyEntryPathAsync(
                "cgus/diar_streaming_sortformer_4spk-v2.1-onnx",
                entryFile,
                variant: null,
                smokeTester,
                CancellationToken.None,
                provider: ExecutionProviderKind.TensorRt);

            Assert.Equal(TrtRtxStarterPackSmokeTargetStatus.Passed, result.Status);
            Assert.NotNull(capturedRequest);
            Assert.Equal(ExecutionProviderKind.TensorRt, capturedRequest!.ExecutionProvider);
        }
        finally
        {
            File.Delete(entryFile);
        }
    }

    private static string CreateCacheWithSingleModel()
    {
        string modelCacheDirectory = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string modelDirectory = Path.Join(
            modelCacheDirectory, "cgus", "diar_streaming_sortformer_4spk-v2.1-onnx");
        Directory.CreateDirectory(modelDirectory);
        File.WriteAllBytes(Path.Join(modelDirectory, "model.onnx"), [0x00]);
        return modelCacheDirectory;
    }

    [Fact]
    public async Task VerifyEntryPathAsync_PassesRequestedEntryPathAndProviderToSmokeTester()
    {
        string entryFile = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(entryFile, [0x00]);
        try
        {
            ExecutionProviderSmokeTestRequest? capturedRequest = null;
            var smokeTester = new FakeExecutionProviderSmokeTester((request, _) =>
            {
                capturedRequest = request;
                return new ExecutionProviderSmokeTestResult(true);
            });

            TrtRtxStarterPackSmokeTargetResult result = await TrtRtxStarterPackSmokeRunner.VerifyEntryPathAsync(
                "cgus/diar_streaming_sortformer_4spk-v2.1-onnx",
                entryFile,
                variant: null,
                smokeTester,
                CancellationToken.None);

            Assert.Equal(TrtRtxStarterPackSmokeTargetStatus.Passed, result.Status);
            Assert.Equal("cgus/diar_streaming_sortformer_4spk-v2.1-onnx", result.ModelReference);
            Assert.NotNull(capturedRequest);
            Assert.Equal(ExecutionProviderKind.TensorRTRtx, capturedRequest!.ExecutionProvider);
            Assert.Equal(RuntimeStage.Diarization, capturedRequest.Stage);
            Assert.Equal(Path.GetFullPath(entryFile), capturedRequest.EntryPath);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(entryFile)), capturedRequest.ModelRootPath);
        }
        finally
        {
            File.Delete(entryFile);
        }
    }

    [Fact]
    public async Task VerifyEntryPathAsync_ReportsSmokeTesterFailureDetail()
    {
        string entryFile = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(entryFile, [0x00]);
        try
        {
            var smokeTester = new FakeExecutionProviderSmokeTester(
                (_, _) => new ExecutionProviderSmokeTestResult(false, "effective provider is cpu, not trt-rtx"));

            TrtRtxStarterPackSmokeTargetResult result = await TrtRtxStarterPackSmokeRunner.VerifyEntryPathAsync(
                "tonythethompson/nemotron-3.5-asr-streaming-0.6b-onnx",
                entryFile,
                variant: null,
                smokeTester,
                CancellationToken.None);

            Assert.Equal(TrtRtxStarterPackSmokeTargetStatus.Failed, result.Status);
            Assert.Equal("effective provider is cpu, not trt-rtx", result.Detail);
        }
        finally
        {
            File.Delete(entryFile);
        }
    }

    [Fact]
    public async Task VerifyEntryPathAsync_ThrowsWhenEntryPathMissing()
    {
        string missingEntryFile = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx");
        var smokeTester = new FakeExecutionProviderSmokeTester(
            (_, _) => throw new InvalidOperationException("Smoke should not run when the entry file is missing."));

        await Assert.ThrowsAsync<FileNotFoundException>(() => TrtRtxStarterPackSmokeRunner.VerifyEntryPathAsync(
            "cgus/diar_streaming_sortformer_4spk-v2.1-onnx",
            missingEntryFile,
            variant: null,
            smokeTester,
            CancellationToken.None));
    }

    [Fact]
    public async Task VerifyEntryPathAsync_ThrowsWhenModelIdNotInManifest()
    {
        string entryFile = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(entryFile, [0x00]);
        try
        {
            var smokeTester = new FakeExecutionProviderSmokeTester(
                (_, _) => throw new InvalidOperationException("Smoke should not run for an unknown model id."));

            await Assert.ThrowsAsync<InvalidOperationException>(() => TrtRtxStarterPackSmokeRunner.VerifyEntryPathAsync(
                "example/does-not-exist",
                entryFile,
                variant: null,
                smokeTester,
                CancellationToken.None));
        }
        finally
        {
            File.Delete(entryFile);
        }
    }

    private sealed class FakeExecutionProviderSmokeTester(
        Func<ExecutionProviderSmokeTestRequest, CancellationToken, ExecutionProviderSmokeTestResult> handler)
        : IExecutionProviderSmokeTester
    {
        public Task<ExecutionProviderSmokeTestResult> SmokeTestAsync(
            ExecutionProviderSmokeTestRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(request, cancellationToken));
    }
}
