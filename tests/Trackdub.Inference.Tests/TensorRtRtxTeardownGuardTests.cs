using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Inference.Tests;

[Collection(nameof(TensorRtRtxTeardownGuardCollection))]
public sealed class TensorRtRtxTeardownGuardTests : IDisposable
{
    public TensorRtRtxTeardownGuardTests() => TensorRtRtxTeardownGuard.ResetForTests();

    public void Dispose() => TensorRtRtxTeardownGuard.ResetForTests();

    [Theory]
    [InlineData("CUDA failure 2: out of memory ; GPU=0 ; hostname=x", true)]
    [InlineData("cudaErrorMemoryAllocation while compiling engine", true)]
    [InlineData("CUDA_ERROR_OUT_OF_MEMORY", true)]
    [InlineData("Kernel not found for op: Squeeze(13) under NvTensorRTRTXExecutionProvider", false)]
    [InlineData("unrelated IO error", false)]
    public void MarkPoisonedIfCudaOutOfMemory_classifies_by_message(string message, bool expected)
    {
        Assert.Equal(expected, TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(new InvalidOperationException(message)));
        Assert.Equal(expected, TensorRtRtxTeardownGuard.IsPoisoned);
    }

    [Fact]
    public void MarkPoisonedIfCudaOutOfMemory_inspects_inner_exceptions()
    {
        var wrapped = new InvalidOperationException("session init failed", new InvalidOperationException("CUDA failure: out of memory"));

        Assert.True(TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(wrapped));
    }

    [Fact]
    public void ShouldSkipNativeTeardown_only_for_tracked_sessions_in_a_poisoned_runtime()
    {
        using InferenceSession tracked = CreateSession();
        using InferenceSession untracked = CreateSession();
        TensorRtRtxTeardownGuard.Track(tracked);

        Assert.False(TensorRtRtxTeardownGuard.ShouldSkipNativeTeardown(tracked));

        TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(new InvalidOperationException("out of memory"));

        Assert.True(TensorRtRtxTeardownGuard.ShouldSkipNativeTeardown(tracked));
        // DirectML/CPU sessions (including fallbacks) are never abandoned.
        Assert.False(TensorRtRtxTeardownGuard.ShouldSkipNativeTeardown(untracked));
    }

    [Fact]
    public void DisposeSafely_after_cuda_oom_leaves_tracked_session_alive()
    {
        InferenceSession session = CreateSession();
        try
        {
            TensorRtRtxTeardownGuard.Track(session);
            Assert.True(TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(
                new InvalidOperationException("CUDA failure: out of memory")));

            // The pool (PoolEntry.Dispose) and the single-session leases both call this.
            // Skipping native teardown is what keeps a poisoned TRT-RTX dispose from
            // faulting the process with 0xC0000005. This fixture is a CPU identity graph,
            // so survival is "the session is still usable and the test process returns".
            TensorRtRtxTeardownGuard.DisposeSafely(session);

            Assert.Equal(1f, RunIdentity(session));
        }
        finally
        {
            TensorRtRtxTeardownGuard.ResetForTests();
            session.Dispose();
        }
    }

    [Fact]
    public void DisposeSafely_still_disposes_sessions_that_are_not_tensorrt()
    {
        InferenceSession session = CreateSession();
        TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(
            new InvalidOperationException("CUDA failure: out of memory"));

        TensorRtRtxTeardownGuard.DisposeSafely(session);

        // A disposed ORT session nulls its native handle. Run then throws
        // NullReferenceException, which is the signal that teardown was not skipped.
        Assert.Throws<NullReferenceException>(() => RunIdentity(session));
    }

    [Fact]
    public void Single_session_and_whisper_leases_skip_native_teardown_after_cuda_oom()
    {
        InferenceSession single = CreateSession();
        InferenceSession encoder = CreateSession();
        InferenceSession decoder = CreateSession();
        try
        {
            TensorRtRtxTeardownGuard.Track(single);
            TensorRtRtxTeardownGuard.Track(encoder);
            TensorRtRtxTeardownGuard.Track(decoder);
            TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(
                new InvalidOperationException("CUDA failure: out of memory"));

            new OnnxExecutionSessionFactory.SingleSessionLease(single, "TensorRT RTX", "TensorRT RTX", null).Dispose();
            new OnnxExecutionSessionFactory.WhisperSessionLease(encoder, decoder, "TensorRT RTX", "TensorRT RTX", null).Dispose();

            Assert.Equal(1f, RunIdentity(single));
            Assert.Equal(1f, RunIdentity(encoder));
            Assert.Equal(1f, RunIdentity(decoder));
        }
        finally
        {
            TensorRtRtxTeardownGuard.ResetForTests();
            single.Dispose();
            encoder.Dispose();
            decoder.Dispose();
        }
    }

    private static float RunIdentity(InferenceSession session)
    {
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run(
            [NamedOnnxValue.CreateFromTensor("x", new DenseTensor<float>(new[] { 1f }, new[] { 1 }))]);
        return Assert.Single(outputs).AsTensor<float>()[0];
    }

    private static InferenceSession CreateSession() => new(
    [
        0x08, 0x07, 0x3A, 0x3A, 0x0A, 0x10, 0x0A, 0x01, 0x78, 0x12, 0x01, 0x79, 0x22, 0x08,
        0x49, 0x64, 0x65, 0x6E, 0x74, 0x69, 0x74, 0x79, 0x12, 0x04, 0x74, 0x65, 0x73, 0x74,
        0x5A, 0x0F, 0x0A, 0x01, 0x78, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01, 0x12, 0x04, 0x0A,
        0x02, 0x08, 0x01, 0x62, 0x0F, 0x0A, 0x01, 0x79, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01,
        0x12, 0x04, 0x0A, 0x02, 0x08, 0x01, 0x42, 0x04, 0x0A, 0x00, 0x10, 0x09,
    ]);
}

/// <summary>The poisoned flag is process-global, so tests that touch it must not run in parallel.</summary>
[CollectionDefinition(nameof(TensorRtRtxTeardownGuardCollection), DisableParallelization = true)]
public sealed class TensorRtRtxTeardownGuardCollection;
