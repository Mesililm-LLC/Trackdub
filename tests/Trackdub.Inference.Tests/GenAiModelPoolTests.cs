using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntimeGenAI;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Pool;
using Xunit;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Lifecycle tests for <see cref="GenAiModelPool"/> using fake resources — no native
/// <see cref="Model"/> construction or model downloads.
/// </summary>
public class GenAiModelPoolTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static InferenceSessionPool NewAdmissionPool(
        long hostBudgetMb = 100, long acceleratorBudgetMb = 100) =>
        new(maxSessions: 8, memoryBudgetMb: acceleratorBudgetMb, hostMemoryBudgetMb: hostBudgetMb);

    private static GenAiModelKey Key(
        string identity,
        long estimatedMb = 64,
        ExecutionProviderKind provider = ExecutionProviderKind.Cpu,
        string? modelId = "m1",
        string? variant = "v1",
        int? deviceId = null,
        string root = "/models/root") =>
        new(identity, modelId, variant, provider, deviceId, estimatedMb, root);

    private static TaskCompletionSource StartSignal(
        ConcurrentDictionary<string, TaskCompletionSource> starts,
        GenAiModelKey key) =>
        starts.GetOrAdd(
            key.ModelRootIdentity,
            static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    private static Func<GenAiModelKey, CancellationToken, Task<IGenAiModelResource>> TrackingFactory(
        List<FakeGenAiModelResource> created)
    {
        return (_, _) =>
        {
            var resource = new FakeGenAiModelResource();
            lock (created)
            {
                created.Add(resource);
            }
            return Task.FromResult<IGenAiModelResource>(resource);
        };
    }

    [Fact]
    public async Task GetLeaseAsync_SameKey_ReusesSingleFactoryAndResource()
    {
        using var admission = NewAdmissionPool();
        var created = new List<FakeGenAiModelResource>();
        using var pool = new GenAiModelPool(admission, TrackingFactory(created));
        var key = Key("a");

        using (GenAiModelLease first = await pool.GetLeaseAsync(key, CancellationToken.None))
        {
            Assert.NotNull(first.Resource);
        }

        using GenAiModelLease second = await pool.GetLeaseAsync(key, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(created);
        Assert.Same(created[0], second.Resource);
    }

    [Fact]
    public async Task GetLeaseAsync_SameKey_ConcurrentWaiter_SerializesWithoutDisposing()
    {
        using var admission = NewAdmissionPool();
        var created = new List<FakeGenAiModelResource>();
        using var pool = new GenAiModelPool(admission, TrackingFactory(created));
        var key = Key("b");

        using GenAiModelLease first = await pool.GetLeaseAsync(key, CancellationToken.None);

        Task<GenAiModelLease> waiter = pool.GetLeaseAsync(key, CancellationToken.None);
        await Task.Delay(150);
        Assert.False(waiter.IsCompleted);

        // A cancelled waiter does not disturb the active lease or its resource.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.GetLeaseAsync(key, cts.Token));
        Assert.Equal(0, created[0].DisposeCount);
        Assert.Single(created);

        first.Dispose();
        using GenAiModelLease second = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(created[0], second.Resource);
    }

    [Fact]
    public async Task GetLeaseAsync_AtCapacity_EvictsOldestIdleBeforeNewFactoryPublishes()
    {
        using var admission = NewAdmissionPool();
        var created = new List<FakeGenAiModelResource>();
        using var pool = new GenAiModelPool(admission, TrackingFactory(created), maxEntries: 1);

        GenAiModelLease first = await pool.GetLeaseAsync(Key("a"), CancellationToken.None);
        first.Dispose(); // idle now

        // maxEntries=1: the second create must evict and dispose the idle first resource.
        using GenAiModelLease second = await pool.GetLeaseAsync(Key("b"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, created.Count);
        Assert.Equal(1, created[0].DisposeCount);
        Assert.Equal(0, created[1].DisposeCount);
    }

    [Fact]
    public async Task GetLeaseAsync_AllEntriesBusy_WaitsAndCancels_WithoutEphemeralCreate()
    {
        using var admission = NewAdmissionPool();
        var created = new List<FakeGenAiModelResource>();
        using var pool = new GenAiModelPool(admission, TrackingFactory(created), maxEntries: 1);

        GenAiModelLease held = await pool.GetLeaseAsync(Key("a"), CancellationToken.None);

        // The only entry is leased: a different key can only wait — no ephemeral create.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.GetLeaseAsync(Key("b"), cts.Token));
        Assert.Single(created);

        held.Dispose();
        using GenAiModelLease after = await pool.GetLeaseAsync(Key("b"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, created.Count);
        Assert.Equal(1, created[0].DisposeCount); // evicted to make room
    }

    [Fact]
    public async Task GetLeaseAsync_FactoryFailure_ReleasesExternalReservation()
    {
        using var admission = NewAdmissionPool(hostBudgetMb: 100);
        int calls = 0;
        using var pool = new GenAiModelPool(
            admission,
            (_, _) =>
            {
                calls++;
                return Task.FromException<IGenAiModelResource>(
                    new InvalidOperationException("synthetic native load failure"));
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.GetLeaseAsync(Key("a", estimatedMb: 80), CancellationToken.None));
        Assert.Equal(1, calls);

        // A full-budget external reservation succeeds only if the failed create left no
        // pending or live accounting behind.
        using ExternalMemoryReservation probe = await admission.ReserveExternalAsync(
            ExecutionProviderKind.Cpu, null, 100, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(probe);
    }

    [Fact]
    public async Task OnnxAdmission_EvictsIdleGenAiReservation()
    {
        using var admission = NewAdmissionPool(hostBudgetMb: 100);
        var created = new List<FakeGenAiModelResource>();
        using var pool = new GenAiModelPool(admission, TrackingFactory(created));
        var onnxKey = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "gen1", 0, "default")
        {
            EstimatedVramMb = 80,
        };

        GenAiModelLease lease = await pool.GetLeaseAsync(Key("a", estimatedMb: 80), CancellationToken.None);
        lease.Dispose(); // idle and eviction-eligible

        using SessionLease onnx = await admission.GetLeaseAsync(
            onnxKey, _ => Task.FromResult(InferenceSessionPoolTests.CreateMinimalSession()),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, created[0].DisposeCount);
        Assert.NotNull(onnx.Session);
    }

    [Fact]
    public async Task OnnxAdmission_CannotEvictBusyGenAi_UntilLeaseReleased()
    {
        using var admission = NewAdmissionPool(hostBudgetMb: 100);
        var created = new List<FakeGenAiModelResource>();
        using var pool = new GenAiModelPool(admission, TrackingFactory(created));
        var onnxKey = new SessionPoolKey("eng", null, null, ExecutionProviderKind.Cpu, "gen2", 0, "default")
        {
            EstimatedVramMb = 80,
        };

        GenAiModelLease held = await pool.GetLeaseAsync(Key("a", estimatedMb: 80), CancellationToken.None);

        // Busy GenAI refuses eviction: the ONNX acquire cancels rather than fitting.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => admission.GetLeaseAsync(
                onnxKey, _ => Task.FromResult(InferenceSessionPoolTests.CreateMinimalSession()), cts.Token));
        Assert.Equal(0, created[0].DisposeCount);

        held.Dispose();
        using SessionLease onnx = await admission.GetLeaseAsync(
            onnxKey, _ => Task.FromResult(InferenceSessionPoolTests.CreateMinimalSession()),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, created[0].DisposeCount);
        Assert.NotNull(onnx.Session);
    }

    [Fact]
    public async Task Dispose_IdleAndLeasedEntries_LeavesSafeTeardown()
    {
        // Host budget 200 fits both 64MB reservations — entry "a" stays leased (busy), so
        // admission cannot evict it and "b" must fit alongside rather than replacing it.
        using var admission = NewAdmissionPool(hostBudgetMb: 200);
        var created = new List<FakeGenAiModelResource>();
        var pool = new GenAiModelPool(admission, TrackingFactory(created), maxEntries: 2);

        GenAiModelLease leased = await pool.GetLeaseAsync(Key("a"), CancellationToken.None);
        using GenAiModelLease idle = await pool.GetLeaseAsync(Key("b"), CancellationToken.None);
        idle.Dispose(); // b idle, a still leased

        pool.Dispose();
        pool.Dispose(); // idempotent

        Assert.Equal(1, created[1].DisposeCount); // idle b disposed by teardown
        Assert.Equal(0, created[0].DisposeCount); // leased a waits for its lease

        leased.Dispose(); // returning lease disposes the evicted entry
        Assert.Equal(1, created[0].DisposeCount);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pool.GetLeaseAsync(Key("c"), CancellationToken.None));
    }

    [Fact]
    public void GenAiModelKey_Equality_AndNormalization()
    {
        var temp = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(Path.Join(temp, "genai_config.json"), "{}");

            var a = GenAiModelKey.Create(temp, ExecutionProviderKind.Cpu, "QWEN", "BF16");
            var b = GenAiModelKey.Create(temp, ExecutionProviderKind.Cpu, "qwen", "bf16");
            var c = GenAiModelKey.Create(temp, ExecutionProviderKind.DirectMl, "qwen", "bf16");
            var d = GenAiModelKey.Create(temp, ExecutionProviderKind.Cpu, "other", "bf16");
            var e = GenAiModelKey.Create(temp, ExecutionProviderKind.Cpu, "qwen", "int8");

            Assert.Equal(a, b);   // id/variant casing normalized
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, c); // provider discriminates
            Assert.NotEqual(a, d); // model id discriminates
            Assert.NotEqual(a, e); // variant discriminates

            var otherRoot = GenAiModelKey.Create(
                Path.Join(temp, "sub"), ExecutionProviderKind.Cpu, "qwen", "bf16");
            Assert.NotEqual(a, otherRoot); // root identity discriminates
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task GenAiModelKey_AsyncIdentity_TracksWeightReplacementAtSamePath()
    {
        string root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string weightsPath = Path.Join(root, "model.onnx_data");
        DateTime timestamp = DateTime.UtcNow.AddMinutes(-5);
        try
        {
            File.WriteAllText(Path.Join(root, "genai_config.json"), "{}");
            File.WriteAllBytes(weightsPath, [0x01, 0x02, 0x03, 0x04]);
            File.SetLastWriteTimeUtc(weightsPath, timestamp);
            GenAiModelKey before = await GenAiModelKey.CreateAsync(root, ExecutionProviderKind.Cpu);

            File.WriteAllBytes(weightsPath, [0x05, 0x06, 0x07, 0x08]);
            File.SetLastWriteTimeUtc(weightsPath, timestamp);
            new SessionPoolModelContentHashCacheInvalidator().Invalidate(weightsPath);
            GenAiModelKey after = await GenAiModelKey.CreateAsync(root, ExecutionProviderKind.Cpu);

            Assert.NotEqual(before.ModelRootIdentity, after.ModelRootIdentity);
        }
        finally
        {
            new SessionPoolModelContentHashCacheInvalidator().Invalidate(weightsPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GenAiModelKey_Estimate_FloorsAndTracksSize()
    {
        Assert.Equal(1024, GenAiModelKey.EstimateMemoryMb(null));
        Assert.Equal(1024, GenAiModelKey.EstimateMemoryMb("/nonexistent/path/that/does/not/exist"));

        var temp = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.Equal(1024, GenAiModelKey.EstimateMemoryMb(temp)); // empty root floors

            File.WriteAllText(Path.Join(temp, "a.bin"), "tiny");
            Assert.Equal(1024, GenAiModelKey.EstimateMemoryMb(temp)); // small still floors

            // Sparse-ish large file: 600MB -> 2*600+256 = 1456 MB.
            string bigPath = Path.Join(temp, "weights.bin");
            using (var stream = new FileStream(bigPath, FileMode.Create))
            {
                stream.SetLength(600L * 1024 * 1024);
            }
            Assert.Equal(1456, GenAiModelKey.EstimateMemoryMb(temp));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task GetLeaseAsync_TwoColdKeysAtCapacity_ContenderNeverHangsOrCreatesEphemeral()
    {
        // TCS-barriered factories make the contention deterministic: A's creation is
        // mid-flight when B arrives at a full pool; B must wait on capacity (never an
        // ephemeral second create), then create exactly once after A's lease frees.
        using var admission = NewAdmissionPool(hostBudgetMb: 400);
        var factoryGates = new ConcurrentDictionary<string, TaskCompletionSource<IGenAiModelResource>>();
        var factoryStarts = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        int factoryCalls = 0;
        using var pool = new GenAiModelPool(
            admission,
            (key, _) =>
            {
                // Count before signalling: a waiter that resumes on the signal must already
                // be able to read this call.
                Interlocked.Increment(ref factoryCalls);
                TaskCompletionSource<IGenAiModelResource> gate = factoryGates
                    .GetOrAdd(key.ModelRootIdentity, static _ => new());
                StartSignal(factoryStarts, key).TrySetResult();
                return gate.Task;
            },
            maxEntries: 1);

        var keyA = Key("race-a");
        var keyB = Key("race-b");
        TaskCompletionSource startA = StartSignal(factoryStarts, keyA);
        TaskCompletionSource startB = StartSignal(factoryStarts, keyB);

        // Await each factory's own start signal instead of polling factoryCalls for a fixed
        // duration: the wait ends the moment the factory runs, and this thread is released
        // rather than spinning for a budget the handoff may need that thread to meet.
        Task<GenAiModelLease> leaseA = pool.GetLeaseAsync(keyA, CancellationToken.None);
        await startA.Task.AwaitWithHangGuard("A's factory never started");

        Task<GenAiModelLease> leaseB = pool.GetLeaseAsync(keyB, CancellationToken.None);
        await Task.Delay(100); // probe window, not synchronization: room for B to create ephemerally
        Assert.False(leaseB.IsCompleted);
        Assert.False(startB.Task.IsCompleted); // B cannot even start its factory while capacity is busy
        Assert.Equal(1, Volatile.Read(ref factoryCalls));

        // Publish A; A stays leased so B still cannot create.
        factoryGates[keyA.ModelRootIdentity].SetResult(new FakeGenAiModelResource());
        using GenAiModelLease heldA = await leaseA.AwaitWithHangGuard("A's lease never resolved");
        await Task.Delay(100); // probe window, as above
        Assert.False(leaseB.IsCompleted);
        Assert.False(startB.Task.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref factoryCalls));

        // Freeing A lets B evict it and create exactly once.
        heldA.Dispose();
        await startB.Task.AwaitWithHangGuard("B's factory never started after A's lease was freed");
        Assert.Equal(2, Volatile.Read(ref factoryCalls));
        factoryGates.GetOrAdd(keyB.ModelRootIdentity, static _ => new())
            .SetResult(new FakeGenAiModelResource());
        using GenAiModelLease finalB = await leaseB.AwaitWithHangGuard("B's lease never resolved");
        Assert.Equal(2, Volatile.Read(ref factoryCalls));
        Assert.NotNull(finalB.Resource);
    }

    [Fact]
    public async Task GetLeaseAsync_CancelledAfterFactoryStarts_DisposesResourceAndReservation()
    {
        // An injected factory may ignore the caller token entirely: the pool must still
        // throw cancellation and dispose both the completed resource and its reservation.
        using var admission = NewAdmissionPool(hostBudgetMb: 100);
        var factoryStarted = new TaskCompletionSource();
        var completeFactory = new TaskCompletionSource<IGenAiModelResource>();
        using var pool = new GenAiModelPool(
            admission,
            (_, _) =>
            {
                factoryStarted.TrySetResult();
                return completeFactory.Task;
            });

        using var cts = new CancellationTokenSource();
        Task<GenAiModelLease> acquire = pool.GetLeaseAsync(Key("c1", estimatedMb: 80), cts.Token);
        await factoryStarted.Task.AwaitWithHangGuard("the factory never started");

        await cts.CancelAsync();
        var fake = new FakeGenAiModelResource();
        completeFactory.SetResult(fake);

        // The assertion is that cancellation is observed. The guard only bounds how long we
        // wait for that verdict, so a loaded runner cannot pass a five-second budget off as
        // the deadlock this test exists to catch.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => acquire.AwaitWithHangGuard("the cancelled acquire never reported cancellation"));
        Assert.Equal(1, fake.DisposeCount);

        // No hidden accounting: a full-bucket reservation must fit.
        using ExternalMemoryReservation probe = await admission.ReserveExternalAsync(
            ExecutionProviderKind.Cpu, null, 100, CancellationToken.None)
            .AwaitWithHangGuard("the post-cancellation accounting probe never completed");
        Assert.NotNull(probe);
    }

    [Fact]
    public async Task GetLeaseAsync_FactoryCompletesDuringPoolDispose_NothingPublishes()
    {
        using var admission = NewAdmissionPool(hostBudgetMb: 100);
        var factoryStarted = new TaskCompletionSource();
        var completeFactory = new TaskCompletionSource<IGenAiModelResource>();
        var pool = new GenAiModelPool(
            admission,
            (_, _) =>
            {
                factoryStarted.TrySetResult();
                return completeFactory.Task;
            });

        Task<GenAiModelLease> acquire = pool.GetLeaseAsync(Key("d1", estimatedMb: 80), CancellationToken.None);
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task disposeTask = Task.Run(pool.Dispose);
        // Deterministic: only complete the factory once disposal has actually marked the
        // pool, so the post-factory check observes it rather than racing the task start.
        Assert.True(SpinWait.SpinUntil(() => pool.IsDisposed, TimeSpan.FromSeconds(5)));

        var fake = new FakeGenAiModelResource();
        completeFactory.SetResult(fake);

        // The create observes disposal between factory completion and publish: the resource
        // and reservation are torn down and the acquire reports ODE instead of a lease.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => acquire.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fake.DisposeCount);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        using ExternalMemoryReservation probe = await admission.ReserveExternalAsync(
            ExecutionProviderKind.Cpu, null, 100, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(probe);
    }

    [Fact]
    public void GenAiModelKey_RevisionHash_DiscriminatesAndNormalizes()
    {
        var temp = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(Path.Join(temp, "genai_config.json"), "{}");
            const string revA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string revB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

            var a = GenAiModelKey.Create(temp, ExecutionProviderKind.Cpu, "m", "v", modelRevisionHash: revA);
            var aUpper = GenAiModelKey.Create(temp, ExecutionProviderKind.Cpu, "m", "v", modelRevisionHash: revA.ToUpperInvariant());
            var b = GenAiModelKey.Create(temp, ExecutionProviderKind.Cpu, "m", "v", modelRevisionHash: revB);
            var none = GenAiModelKey.Create(temp, ExecutionProviderKind.Cpu, "m", "v");

            Assert.Equal(a, aUpper);  // digest casing normalized
            Assert.NotEqual(a, b);    // same path/config/id/variant, different resolved revision
            Assert.NotEqual(a, none); // resolved revision vs fallback identity
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task GetLeaseAsync_RecordsHitMissCreateCounters()
    {
        var capture = new BenchmarkPhaseCapture();
        using IDisposable? activation = BenchmarkPhaseCapture.Activate(capture);
        using var admission = NewAdmissionPool();
        var created = new List<FakeGenAiModelResource>();
        using var pool = new GenAiModelPool(admission, TrackingFactory(created));
        var key = Key("ctr");

        // Cold request: exactly one miss classification, one create, zero hits.
        using (await pool.GetLeaseAsync(key, CancellationToken.None))
        {
        }

        IReadOnlyDictionary<string, long> counts = capture.SnapshotCounters();
        Assert.Equal(1L, counts["genAiModelMiss"]);
        Assert.Equal(1L, counts["genAiModelCreate"]);
        Assert.False(counts.ContainsKey("genAiModelHit"));

        // Warm request: one hit, still one create and one miss cumulatively.
        using (await pool.GetLeaseAsync(key, CancellationToken.None))
        {
        }

        counts = capture.SnapshotCounters();
        Assert.Equal(1L, counts["genAiModelMiss"]);
        Assert.Equal(1L, counts["genAiModelHit"]);
        Assert.Equal(1L, counts["genAiModelCreate"]);
    }

    [Fact]
    public async Task GetLeaseAsync_MissNotDoubleCounted_WhenEntryAppearsDuringWait()
    {
        using var admission = NewAdmissionPool();
        var capture = new BenchmarkPhaseCapture();
        using IDisposable? activation = BenchmarkPhaseCapture.Activate(capture);
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pool = new GenAiModelPool(
            admission,
            async (_, _) =>
            {
                factoryStarted.SetResult();
                await factoryReady.Task.ConfigureAwait(false);
                return new FakeGenAiModelResource();
            });
        var key = Key("ctr2");

        Task<GenAiModelLease> creator = pool.GetLeaseAsync(key, CancellationToken.None);
        await factoryStarted.Task.WaitAsync(HangGuard);

        // Waiter arrives mid-create: its request is a miss at arrival and must stay one miss.
        // The miss is recorded before the waiter's first await (the creator holds the create
        // gate), so it is observable as soon as GetLeaseAsync returns.
        Task<GenAiModelLease> waiter = pool.GetLeaseAsync(key, CancellationToken.None);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(2L, capture.SnapshotCounters().GetValueOrDefault("genAiModelMiss"));

        // Completing the factory resumes the pool on thread-pool continuations, so these are
        // hang guards sized for a saturated CI runner, not timing assertions.
        factoryReady.SetResult();
        using GenAiModelLease first = await creator.WaitAsync(HangGuard);
        first.Dispose();
        using GenAiModelLease second = await waiter.WaitAsync(HangGuard);

        IReadOnlyDictionary<string, long> counts = capture.SnapshotCounters();
        Assert.Equal(1L, counts["genAiModelCreate"]);
        Assert.Equal(2L, counts["genAiModelMiss"]);
        Assert.False(counts.ContainsKey("genAiModelHit"));
    }

    [Fact]
    public async Task GetLeaseAsync_AdmissionDisposedMidCreate_RollsBackPublish()
    {
        var admission = NewAdmissionPool(hostBudgetMb: 100);
        var factoryStarted = new TaskCompletionSource();
        var completeFactory = new TaskCompletionSource<IGenAiModelResource>();
        var pool = new GenAiModelPool(
            admission,
            (_, _) =>
            {
                factoryStarted.TrySetResult();
                return completeFactory.Task;
            });

        Task<GenAiModelLease> acquire = pool.GetLeaseAsync(
            Key("x1", estimatedMb: 80), CancellationToken.None);
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The reservation is live but its idle callback is not registered yet — admission
        // disposal must remove the accounting so publish-time registration fails.
        admission.Dispose();

        var fake = new FakeGenAiModelResource();
        completeFactory.SetResult(fake);

        // Rollback: acquire fails, the completed resource is disposed exactly once, and
        // nothing unaccounted stays published.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => acquire.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fake.DisposeCount);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pool.GetLeaseAsync(Key("x2"), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        pool.Dispose();
    }

    private sealed class FakeGenAiModelResource : IGenAiModelResource
    {
        private int disposeCount;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public Model Model => throw new NotSupportedException("Fake resource has no native model.");

        public void Dispose() => Interlocked.Increment(ref disposeCount);
    }
}
