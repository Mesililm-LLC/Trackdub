using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Unit tests for <see cref="SidecarCache{T}"/>.
/// These tests are pure (no I/O, no ONNX runtime).
/// </summary>
public sealed class SidecarCacheTests
{
    // ── GetOrAdd ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetOrAdd_FirstCall_InvokesFactory()
    {
        var cache = new SidecarCache<string>();
        int callCount = 0;

        string result = cache.GetOrAdd("key", () =>
        {
            callCount++;
            return "value";
        });

        Assert.Equal("value", result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void GetOrAdd_SubsequentCallWithSameKey_DoesNotInvokeFactory()
    {
        var cache = new SidecarCache<string>();
        int callCount = 0;

        Func<string> factory = () =>
        {
            callCount++;
            return "value";
        };

        _ = cache.GetOrAdd("key", factory);
        _ = cache.GetOrAdd("key", factory);
        _ = cache.GetOrAdd("key", factory);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void GetOrAdd_DifferentKeys_EachInvokesFactory()
    {
        var cache = new SidecarCache<string>();
        int callCount = 0;

        _ = cache.GetOrAdd("key1", () => { callCount++; return "v1"; });
        _ = cache.GetOrAdd("key2", () => { callCount++; return "v2"; });

        Assert.Equal(2, callCount);
    }

    [Fact]
    public void GetOrAdd_ReturnsSameObjectInstance_ForSameKey()
    {
        var cache = new SidecarCache<object>();
        var obj = new object();

        object first = cache.GetOrAdd("key", () => obj);
        object second = cache.GetOrAdd("key", () => new object()); // different factory

        Assert.Same(first, second);
    }

    [Fact]
    public void GetOrAdd_KeyComparison_IsWindowsCaseInsensitive_OtherwiseCaseSensitive()
    {
        var cache = new SidecarCache<string>();
        int callCount = 0;

        _ = cache.GetOrAdd("MyKey", () => { callCount++; return "value"; });
        _ = cache.GetOrAdd("MYKEY", () => { callCount++; return "value"; });
        _ = cache.GetOrAdd("mykey", () => { callCount++; return "value"; });

        int expectedCallCount = OperatingSystem.IsWindows() ? 1 : 3;
        Assert.Equal(expectedCallCount, callCount);
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_ExistingKey_ReturnsTrueAndFactoryIsCalledOnNextGet()
    {
        var cache = new SidecarCache<string>();
        int callCount = 0;

        _ = cache.GetOrAdd("key", () => { callCount++; return "v1"; });
        bool removed = cache.Remove("key");
        _ = cache.GetOrAdd("key", () => { callCount++; return "v2"; });

        Assert.True(removed);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void Remove_NonExistentKey_ReturnsFalse()
    {
        var cache = new SidecarCache<string>();

        bool removed = cache.Remove("no-such-key");

        Assert.False(removed);
    }

    [Fact]
    public void TryRemove_CreatedEntry_ReturnsValue()
    {
        var cache = new SidecarCache<string>();
        _ = cache.GetOrAdd("key", () => "value");

        bool removed = cache.TryRemove("key", out string? value);

        Assert.True(removed);
        Assert.Equal("value", value);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task TryRemove_InFlightEntry_DoesNotForceValueCreation()
    {
        var cache = new SidecarCache<string>();
        var factoryStarted = new ManualResetEventSlim();
        var releaseFactory = new ManualResetEventSlim();
        try
        {
            Task<string> getTask = StartLongRunning(() => cache.GetOrAdd("key", () =>
            {
                factoryStarted.Set();
                releaseFactory.Wait();
                return "value";
            }));

            Assert.True(factoryStarted.Wait(TimeSpan.FromSeconds(5)));

            bool removed = cache.TryRemove("key", out string? value);
            releaseFactory.Set();
            string created = await getTask;

            Assert.True(removed);
            Assert.Null(value);
            Assert.Equal("value", created);
            Assert.Equal(0, cache.Count);
        }
        finally
        {
            releaseFactory.Dispose();
            factoryStarted.Dispose();
        }
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new SidecarCache<string>();
        _ = cache.GetOrAdd("k1", () => "v1");
        _ = cache.GetOrAdd("k2", () => "v2");

        cache.Clear();

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Clear_AfterClear_FactoryIsCalledAgain()
    {
        var cache = new SidecarCache<string>();
        int callCount = 0;

        _ = cache.GetOrAdd("key", () => { callCount++; return "v1"; });
        cache.Clear();
        _ = cache.GetOrAdd("key", () => { callCount++; return "v2"; });

        Assert.Equal(2, callCount);
    }

    [Fact]
    public void Clear_WithEvictionCallback_InvokesForCreatedValues()
    {
        var cache = new SidecarCache<string>();
        _ = cache.GetOrAdd("key1", () => "value1");
        _ = cache.GetOrAdd("key2", () => "value2");
        var evicted = new List<string>();

        cache.Clear(evicted.Add);

        Assert.Equal(["value1", "value2"], evicted.OrderBy(value => value));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Clear_WithEvictionCallback_DoesNotForceInFlightValueCreation()
    {
        var cache = new SidecarCache<string>();
        var factoryStarted = new ManualResetEventSlim();
        var releaseFactory = new ManualResetEventSlim();
        try
        {
            var evicted = new List<string>();
            Task<string> getTask = StartLongRunning(() => cache.GetOrAdd("key", () =>
            {
                factoryStarted.Set();
                releaseFactory.Wait();
                return "value";
            }));

            Assert.True(factoryStarted.Wait(TimeSpan.FromSeconds(5)));

            cache.Clear(evicted.Add);
            releaseFactory.Set();
            string created = await getTask;

            Assert.Empty(evicted);
            Assert.Equal("value", created);
            Assert.Equal(0, cache.Count);
        }
        finally
        {
            releaseFactory.Dispose();
            factoryStarted.Dispose();
        }
    }

    [Fact]
    public async Task Clear_WithEvictionCallback_EvictsCompletedAsyncValues()
    {
        var cache = new SidecarCache<string>();

        _ = await cache.GetOrAddAsync("key1", async _ => { await Task.Yield(); return "value1"; });
        _ = await cache.GetOrAddAsync("key2", async _ => { await Task.Yield(); return "value2"; });
        var evicted = new List<string>();

        cache.Clear(evicted.Add);

        Assert.Equal(["value1", "value2"], evicted.OrderBy(value => value));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Clear_WithEvictionCallback_DoesNotForceInFlightAsyncValueCreation()
    {
        var cache = new SidecarCache<string>();
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evicted = new List<string>();

        Task<string> getTask = cache.GetOrAddAsync("key", async _ =>
        {
            factoryStarted.TrySetResult();
            await releaseFactory.Task.ConfigureAwait(false);
            return "value";
        });

        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cache.Clear(evicted.Add);
        releaseFactory.TrySetResult();
        string created = await getTask;

        Assert.Empty(evicted);
        Assert.Equal("value", created);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Clear_WithEvictionCallback_EvictsBothSyncAndAsyncValues()
    {
        var cache = new SidecarCache<string>();

        _ = cache.GetOrAdd("sync", () => "sync-value");
        _ = await cache.GetOrAddAsync("async", async _ => { await Task.Yield(); return "async-value"; });
        var evicted = new List<string>();

        cache.Clear(evicted.Add);

        Assert.Equal(["async-value", "sync-value"], evicted.OrderBy(value => value));
        Assert.Equal(0, cache.Count);
    }

    // ── Count ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrAddAsync_CancellingOneCallersWait_DoesNotCancelTheSharedLoad()
    {
        // Pins the cache primitive the Qwen3-ASR engine relies on: GetOrAddAsync shares one load
        // per key, and a caller's own WaitAsync must be able to give up without cancelling that
        // shared work. This does NOT exercise the engine itself, so it would not catch the engine
        // regressing to pass its caller's token into the load factory; it only guarantees the
        // primitive keeps offering the isolation the engine's CancellationToken.None call needs.
        var cache = new SidecarCache<string>();
        var load = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        int factoryCalls = 0;

        Task<string> cancelledCaller = cache
            .GetOrAddAsync("model", _ =>
            {
                factoryCalls++;
                return load.Task;
            })
            .WaitAsync(cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCaller);

        load.SetResult("loaded");
        string sharedValue = await cache.GetOrAddAsync("model", _ => Task.FromResult("replacement"));

        Assert.Equal("loaded", sharedValue);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void Count_ReflectsDistinctKeys()
    {
        var cache = new SidecarCache<int>();

        Assert.Equal(0, cache.Count);

        _ = cache.GetOrAdd("a", () => 1);
        Assert.Equal(1, cache.Count);

        _ = cache.GetOrAdd("b", () => 2);
        Assert.Equal(2, cache.Count);

        _ = cache.GetOrAdd("a", () => 99); // duplicate — should not increase count
        Assert.Equal(2, cache.Count);
    }

    // ── Fault handling ────────────────────────────────────────────────────────

    [Fact]
    public void GetOrAdd_FactoryThrows_PropagatesExceptionAndEvictsEntry()
    {
        var cache = new SidecarCache<string>();
        int callCount = 0;

        // First call: factory throws.
        Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrAdd("key", () =>
            {
                callCount++;
                throw new InvalidOperationException("load failed");
            }));

        Assert.Equal(1, callCount);
        Assert.Equal(0, cache.Count); // faulted entry must have been evicted

        // Second call with a succeeding factory: must retry and succeed.
        string result = cache.GetOrAdd("key", () =>
        {
            callCount++;
            return "recovered";
        });

        Assert.Equal("recovered", result);
        Assert.Equal(2, callCount); // factory called again because faulted entry was evicted
        Assert.Equal(1, cache.Count); // successful entry is now cached
    }

    [Fact]
    public void GetOrAdd_FactoryThrows_SubsequentSuccessfulCallIsCached()
    {
        var cache = new SidecarCache<int>();
        int callCount = 0;

        // First call: factory throws.
        Assert.ThrowsAny<Exception>(() =>
            cache.GetOrAdd("key", () => { callCount++; throw new Exception("boom"); }));

        // Second call: succeeds.
        _ = cache.GetOrAdd("key", () => { callCount++; return 42; });

        // Third call: should return cached value, NOT call factory again.
        int result = cache.GetOrAdd("key", () => { callCount++; return 99; });

        Assert.Equal(42, result);
        Assert.Equal(2, callCount); // only the two explicit calls above
    }

    // ── Thread safety ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrAdd_ConcurrentCalls_SameKey_FactoryCalledOnce()
    {
        var cache = new SidecarCache<string>();
        int callCount = 0;
        var barrier = new Barrier(10);
        try
        {
            var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return cache.GetOrAdd("key", () =>
                {
                    Interlocked.Increment(ref callCount);
                    return "value";
                });
            })).ToArray();

            string[] results = await Task.WhenAll(tasks);

            Assert.Equal(1, callCount);
            Assert.All(results, r => Assert.Equal("value", r));
        }
        finally
        {
            barrier.Dispose();
        }
    }

    private static Task<T> StartLongRunning<T>(Func<T> action) =>
        Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
}
