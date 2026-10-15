using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Inference.Tests;

[Collection(nameof(TensorRtRtxTeardownGuardCollection))]
public sealed class TensorRtRtxTeardownGuardTests : IDisposable
{
    public TensorRtRtxTeardownGuardTests() => TensorRtRtxTeardownGuard.ResetForTests();

    public void Dispose() => TensorRtRtxTeardownGuard.ResetForTests();

    [Theory]
    [InlineData("CUDA failure 2: out of memory ; GPU=0 ; hostname=x", true)]
    [InlineData("CUDA failure 2", true)]
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

        Assert.True(TensorRtRtxTeardownGuard.IsTracked(tracked));
        Assert.False(TensorRtRtxTeardownGuard.IsTracked(untracked));
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
    public void Single_session_and_compound_leases_skip_native_teardown_after_cuda_oom()
    {
        InferenceSession single = CreateSession();
        InferenceSession whisperEncoder = CreateSession();
        InferenceSession whisperDecoder = CreateSession();
        InferenceSession opusEncoder = CreateSession();
        InferenceSession opusDecoder = CreateSession();
        InferenceSession qwenEncoder = CreateSession();
        InferenceSession qwenDecoderInit = CreateSession();
        InferenceSession qwenDecoderStep = CreateSession();
        InferenceSession nemotronEncoder = CreateSession();
        InferenceSession nemotronDecoderJoint = CreateSession();
        InferenceSession latentUnet = CreateSession();
        InferenceSession latentVaeEnc = CreateSession();
        InferenceSession latentVaeDec = CreateSession();
        InferenceSession latentWhisperEnc = CreateSession();

        InferenceSession[] allSessions =
        [
            single,
            whisperEncoder,
            whisperDecoder,
            opusEncoder,
            opusDecoder,
            qwenEncoder,
            qwenDecoderInit,
            qwenDecoderStep,
            nemotronEncoder,
            nemotronDecoderJoint,
            latentUnet,
            latentVaeEnc,
            latentVaeDec,
            latentWhisperEnc,
        ];

        try
        {
            foreach (InferenceSession session in allSessions)
            {
                TensorRtRtxTeardownGuard.Track(session);
            }

            TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(
                new InvalidOperationException("CUDA failure 2: out of memory"));

            new OnnxExecutionSessionFactory.SingleSessionLease(single, "tensorrt-rtx", "tensorrt-rtx", null).Dispose();
            new OnnxExecutionSessionFactory.WhisperSessionLease(whisperEncoder, whisperDecoder, "tensorrt-rtx", "tensorrt-rtx", null).Dispose();
            new OnnxExecutionSessionFactory.OpusSessionLease(opusEncoder, opusDecoder, "tensorrt-rtx", "tensorrt-rtx", null).Dispose();
            new OnnxExecutionSessionFactory.Qwen3AsrSessionLease(qwenEncoder, qwenDecoderInit, qwenDecoderStep, "tensorrt-rtx", "tensorrt-rtx", null).Dispose();
            new OnnxExecutionSessionFactory.NemotronAsrSessionLease(nemotronEncoder, nemotronDecoderJoint, "tensorrt-rtx", "tensorrt-rtx", null).Dispose();
            new OnnxExecutionSessionFactory.LatentSyncSessionLease(latentUnet, latentVaeEnc, latentVaeDec, latentWhisperEnc, "tensorrt-rtx", "tensorrt-rtx", null).Dispose();

            foreach (InferenceSession session in allSessions)
            {
                Assert.Equal(1f, RunIdentity(session));
            }
        }
        finally
        {
            TensorRtRtxTeardownGuard.ResetForTests();
            foreach (InferenceSession session in allSessions)
            {
                session.Dispose();
            }
        }
    }

    [Fact]
    public void Compound_leases_still_dispose_untracked_fallback_sessions_after_cuda_oom()
    {
        InferenceSession qwenEncoder = CreateSession();
        InferenceSession qwenDecoderInit = CreateSession();
        InferenceSession qwenDecoderStep = CreateSession();
        InferenceSession nemotronEncoder = CreateSession();
        InferenceSession nemotronDecoderJoint = CreateSession();
        InferenceSession latentUnet = CreateSession();
        InferenceSession latentVaeEnc = CreateSession();
        InferenceSession latentVaeDec = CreateSession();
        InferenceSession latentWhisperEnc = CreateSession();

        TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(
            new InvalidOperationException("CUDA failure 2: out of memory"));

        new OnnxExecutionSessionFactory.Qwen3AsrSessionLease(qwenEncoder, qwenDecoderInit, qwenDecoderStep, "tensorrt-rtx", "cpu", null).Dispose();
        new OnnxExecutionSessionFactory.NemotronAsrSessionLease(nemotronEncoder, nemotronDecoderJoint, "tensorrt-rtx", "cpu", null).Dispose();
        new OnnxExecutionSessionFactory.LatentSyncSessionLease(latentUnet, latentVaeEnc, latentVaeDec, latentWhisperEnc, "tensorrt-rtx", "cpu", null).Dispose();

        Assert.Throws<NullReferenceException>(() => RunIdentity(qwenEncoder));
        Assert.Throws<NullReferenceException>(() => RunIdentity(qwenDecoderInit));
        Assert.Throws<NullReferenceException>(() => RunIdentity(qwenDecoderStep));
        Assert.Throws<NullReferenceException>(() => RunIdentity(nemotronEncoder));
        Assert.Throws<NullReferenceException>(() => RunIdentity(nemotronDecoderJoint));
        Assert.Throws<NullReferenceException>(() => RunIdentity(latentUnet));
        Assert.Throws<NullReferenceException>(() => RunIdentity(latentVaeEnc));
        Assert.Throws<NullReferenceException>(() => RunIdentity(latentVaeDec));
        Assert.Throws<NullReferenceException>(() => RunIdentity(latentWhisperEnc));
    }

    [Fact]
    public void Create_failure_marks_cuda_oom_before_disposing_the_tracked_encoder()
    {
        InferenceSession encoder = CreateSession();
        try
        {
            TensorRtRtxTeardownGuard.Track(encoder);
            Assert.False(TensorRtRtxTeardownGuard.IsPoisoned);

            OnnxExecutionSessionFactory.ReleaseSessionsAfterCreateFailure(
                new InvalidOperationException("CUDA failure: out of memory"),
                encoder,
                decoder: null);

            Assert.True(TensorRtRtxTeardownGuard.IsPoisoned);
            Assert.Equal(1f, RunIdentity(encoder));
        }
        finally
        {
            TensorRtRtxTeardownGuard.ResetForTests();
            encoder.Dispose();
        }
    }

    [Fact]
    public void Create_failure_still_disposes_when_the_error_is_not_cuda_oom()
    {
        InferenceSession encoder = CreateSession();
        TensorRtRtxTeardownGuard.Track(encoder);

        OnnxExecutionSessionFactory.ReleaseSessionsAfterCreateFailure(
            new InvalidOperationException("unrelated IO error"),
            encoder,
            decoder: null);

        Assert.False(TensorRtRtxTeardownGuard.IsPoisoned);
        Assert.Throws<NullReferenceException>(() => RunIdentity(encoder));
    }

    [Fact]
    public void Cpu_fallback_is_untracked_and_still_disposes_after_cuda_oom()
    {
        InferenceSession session = CreateSession();
        TensorRtRtxTeardownGuard.Track(session);
        TensorRtRtxTeardownGuard.ReconcileTracking(session, ExecutionProviderKind.Cpu);
        TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(
            new InvalidOperationException("CUDA failure: out of memory"));

        TensorRtRtxTeardownGuard.DisposeSafely(session);

        Assert.Throws<NullReferenceException>(() => RunIdentity(session));
    }

    [Fact]
    public async Task Pool_eviction_under_poisoned_state_skips_trt_teardown_while_disposing_non_trt_entries()
    {
        InferenceSession trtSession = CreateSession();
        InferenceSession cpuSession = CreateSession();
        try
        {
            using var pool = new InferenceSessionPool(maxSessions: 4, enableMemoryAdmission: false);
            var trtKey = new SessionPoolKey(
                "whisper-onnx",
                "whisper-large-v3",
                "default",
                ExecutionProviderKind.TensorRTRtx,
                "encoder_model.onnx",
                DeviceId: 0,
                GraphRole: "encoder");
            var failedTrtDecoderKey = new SessionPoolKey(
                "whisper-onnx",
                "whisper-large-v3",
                "default",
                ExecutionProviderKind.TensorRTRtx,
                "decoder_model.onnx",
                DeviceId: 0,
                GraphRole: "decoder");
            var cpuKey = new SessionPoolKey(
                "whisper-onnx",
                "whisper-large-v3",
                "default",
                ExecutionProviderKind.Cpu,
                "encoder_model.onnx",
                DeviceId: null,
                GraphRole: "encoder");

            using (SessionLease trtLease = await pool.GetLeaseAsync(
                trtKey,
                _ => Task.FromResult(trtSession),
                CancellationToken.None))
            {
                Assert.True(TensorRtRtxTeardownGuard.IsTracked(trtLease.Session));
            }

            using (SessionLease cpuLease = await pool.GetLeaseAsync(
                cpuKey,
                _ => Task.FromResult(cpuSession),
                CancellationToken.None))
            {
                Assert.False(TensorRtRtxTeardownGuard.IsTracked(cpuLease.Session));
            }

            // Simulate decoder engine compilation throwing CUDA OOM on TensorRT-RTX.
            await Assert.ThrowsAsync<InvalidOperationException>(() => pool.GetLeaseAsync(
                failedTrtDecoderKey,
                _ => throw new InvalidOperationException("[NvTensorRTRTX EP] CUDA failure 2: out of memory"),
                CancellationToken.None));

            Assert.True(TensorRtRtxTeardownGuard.IsPoisoned);

            int evicted = await pool.EvictAllIdleAsync();
            Assert.Equal(2, evicted);

            // The poisoned TRT-RTX session skipped native Dispose; the CPU session disposed cleanly.
            Assert.Equal(1f, RunIdentity(trtSession));
            Assert.Throws<NullReferenceException>(() => RunIdentity(cpuSession));
        }
        finally
        {
            TensorRtRtxTeardownGuard.ResetForTests();
            trtSession.Dispose();
            cpuSession.Dispose();
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
