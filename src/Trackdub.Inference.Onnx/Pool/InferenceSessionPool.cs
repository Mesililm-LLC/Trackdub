using System.Collections.Concurrent;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Bounded, LRU-evicting pool of ONNX <see cref="InferenceSession"/> instances shared
/// across engine instances within a process.
/// </summary>
/// <remarks>
/// <para><strong>Lifecycle &amp; lease shape:</strong>
/// Callers obtain an exclusive <see cref="SessionLease"/> via <see cref="GetLeaseAsync"/>.
/// The lease holds the session until disposed; disposal releases the session back to the pool
/// without destroying it.  This allows warm sessions to survive across DI scope boundaries.</para>
///
/// <para><strong>Bounded capacity:</strong>
/// The pool holds at most <c>maxSessions</c> live sessions across all keys (default: 12).
/// This is a <em>count-based</em> cap that applies when memory admission is explicitly
/// disabled. Memory admission (on by default) adds a resident-memory budget per bucket:
/// before construct, reserve <see cref="SessionPoolKey.EstimatedVramMb"/>, evict idle
/// sessions to fit, and wait — never allocate an unbudgeted ephemeral session. CPU, DNNL,
/// and OpenVINO CPU-proxy sessions are accounted against the host RAM budget; OpenVINO NPU
/// sessions and other accelerators are accounted per device, so CPU work no longer
/// collides with GPU 0. A session whose factory falls back to a provider in another bucket
/// while it is created (see <see cref="GetLeaseReportingProviderAsync"/>) is re-admitted and
/// accounted in that bucket instead of its key's. When admission is explicitly
/// disabled and the count limit is reached with every entry leased, the new session is
/// created outside the pool (ephemeral) and disposed when its lease is released.</para>
///
/// <para><strong>Process-isolated VRAM observation:</strong>
/// When the host registers a process-GPU reader (see
/// <see cref="SharedPoolOptions.UseProcessGpuMemoryReader"/>), accelerator admission also floors
/// each device's admitted usage at this process's real dedicated GPU footprint, so GPU memory the
/// pool does not account for — driver contexts, runtime arenas, non-pooled consumers — consumes
/// the same per-device budget instead of being invisible to admission. Host-RAM buckets are
/// unaffected, an unavailable reading leaves reservation accounting exactly as it was, and
/// <c>TRACKDUB_SESSION_PROCESS_GPU_ADMISSION</c> disables the observation explicitly.</para>
///
/// <para><strong>Single-flight creation:</strong>
/// Concurrent misses for the same key share one factory invocation. Cancelling one waiter
/// does not cancel another caller’s valid acquisition.</para>
///
/// <para><strong>Model invalidation:</strong>
/// Session keys carry both the model file <em>path hash</em> and a cached content digest
/// (<see cref="SessionPoolKey.ModelContentHash"/>): helper-built keys invalidate when a
/// model file is replaced at the same path, while direct-constructed keys that omit the
/// content hash keep path-only identity.</para>
///
/// <para><strong>Memory pressure guidance:</strong>
/// Each ONNX session can consume hundreds of MB of accelerator or host memory depending on
/// model size. Admission enforces realistic budgets; call <see cref="EvictModelAsync"/> to
/// free memory for a model that is no longer needed, or <see cref="Dispose"/> to release
/// the entire pool. The default host limit can reject large graphs and graph bundles; see
/// <c>docs/reference/session-pool-memory-admission.md</c> for sizing and the explicit override.</para>
///
/// <para><strong>Thread safety:</strong>
/// All public APIs are thread-safe.  Sessions are single-threaded: only one caller may hold
/// a lease for a given key at a time, serialised by an internal per-entry gate.</para>
/// </remarks>
internal sealed class InferenceSessionPool : IDisposable
{
    /// <summary>Process-wide shared pool with default settings.</summary>
    /// <remarks>
    /// All production engines (Kokoro, Chatterbox, Whisper, Opus-MT, MADLAD, SepFormer,
    /// Spleeter, SortFormer, Silero-VAD) consume this pool via the
    /// <c>CreatePooled*</c> factory methods in <see cref="OnnxExecutionSessionFactory"/>.
    /// </remarks>
    /// <remarks>
    /// The shared instance is built from <see cref="SharedPoolOptions"/>, which reads
    /// <c>TRACKDUB_SESSION_ADMISSION</c>, <c>TRACKDUB_SESSION_VRAM_BUDGET_MB</c>,
    /// <c>TRACKDUB_SESSION_RAM_BUDGET_MB</c>, and <c>TRACKDUB_SESSION_MAX_SESSIONS</c>.
    /// It is resolved lazily on first use. Admission
    /// is <em>on</em> by default — the safe behaviour; an operator can opt out explicitly for
    /// diagnostics. See <see cref="SharedPoolOptions"/> for the activation contract.
    /// </remarks>
    public static InferenceSessionPool Shared => SharedPool.Value;

    private static readonly Lazy<InferenceSessionPool> SharedPool = new(
        static () => new InferenceSessionPool(
            maxSessions: SharedPoolOptions.MaxSessions,
            enableMemoryAdmission: SharedPoolOptions.EnableMemoryAdmission,
            memoryBudgetMb: SharedPoolOptions.MemoryBudgetMb,
            hostMemoryBudgetMb: SharedPoolOptions.HostMemoryBudgetMb,
            // Resolved late: a host registers its process-GPU reader through
            // SharedPoolOptions.UseProcessGpuMemoryReader, which can happen after this static
            // pool was first resolved.
            processGpuMemoryReader: static () => SharedPoolOptions.ProcessGpuMemoryReader),
        LazyThreadSafetyMode.ExecutionAndPublication);

    // Sized for a full dub working set (VAD + separation + diarization + ASR encoder/decoder
    // + translation encoder/decoder + multi-graph TTS) so LRU does not thrash mid-pipeline.
    public const int DefaultMaxSessions = 12;

    /// <summary>
    /// Default accelerator (VRAM) admission budget per device when <c>enableMemoryAdmission</c> is on.
    /// Scaled with detected GPU VRAM (three-quarters of the largest adapter, floor 4GB,
    /// cap 16GB) so large bundles such as MADLAD-400 (~6.4GB quantized reservation at 2x, ~8.1GB
    /// for the external-data fp16 export on TensorRT RTX) fit on 12GB GPUs out-of-the-box,
    /// while small GPUs keep the conservative 4GB guard.
    /// Deliberately a larger fraction than the host-RAM default (quarter): VRAM is the
    /// working set's home, and reservations already include weights plus slack (2x on
    /// unmeasured providers, a measured 1.25x on TensorRT RTX and DirectML — see
    /// <see cref="SessionPoolKey.ResidentPercentOfWeights"/>), while process-GPU observation
    /// charges any real usage beyond them.
    /// An explicit <c>TRACKDUB_SESSION_VRAM_BUDGET_MB</c> always wins over this default.
    /// Unknown/unavailable VRAM (non-Windows, no GPU, detection failure) falls back to
    /// the 4GB floor — never lower than the historical default.
    /// </summary>
    public static long DefaultMemoryBudgetMb { get; } = ResolveDefaultMemoryBudgetMb();

    /// <summary>Floor for the scaled accelerator budget: the historical conservative guard.</summary>
    public const long AcceleratorBudgetFloorMb = 4096;

    /// <summary>Cap for the scaled accelerator budget.</summary>
    public const long AcceleratorBudgetCapMb = 16384;

    internal static long ScaleAcceleratorBudgetMb(long maxVramMb) =>
        Math.Clamp(maxVramMb * 3 / 4, AcceleratorBudgetFloorMb, AcceleratorBudgetCapMb);

    internal static long DetectMaxAcceleratorVramMb()
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows())
            return 0;

        try
        {
            return AcceleratorVramProbe.QueryMaxDedicatedVramMb();
        }
        catch
        {
            return 0;
        }
#else
        if (!OperatingSystem.IsLinux())
            return 0;

        try
        {
            return AcceleratorVramProbe.QueryLinuxMaxDedicatedVramMb();
        }
        catch
        {
            return 0;
        }
#endif
    }

    private static long ResolveDefaultMemoryBudgetMb()
    {
        try
        {
            return ScaleAcceleratorBudgetMb(DetectMaxAcceleratorVramMb());
        }
        catch
        {
            return AcceleratorBudgetFloorMb;
        }
    }

    /// <summary>
    /// Default host RAM admission budget shared by CPU/DNNL and OpenVINO CPU-proxy sessions.
    /// Scaled with physical RAM (quarter of total, floor 4GB, cap 16GB) so large CPU
    /// bundles such as MADLAD-400 (~6.4GB reservation) are admissible out-of-the-box on
    /// ample machines, while small machines keep the conservative 4GB guard.
    /// An explicit <c>TRACKDUB_SESSION_RAM_BUDGET_MB</c> always wins over this default.
    /// </summary>
    public static long DefaultHostMemoryBudgetMb { get; } = ResolveDefaultHostMemoryBudgetMb();

    /// <summary>Floor for the scaled host RAM budget: the historical conservative guard.</summary>
    public const long HostMemoryBudgetFloorMb = 4096;

    /// <summary>Cap for the scaled host RAM budget.</summary>
    public const long HostMemoryBudgetCapMb = 16384;

    internal static long ScaleHostMemoryBudgetMb(long totalRamMb) =>
        Math.Clamp(totalRamMb / 4, HostMemoryBudgetFloorMb, HostMemoryBudgetCapMb);

    private static long ResolveDefaultHostMemoryBudgetMb()
    {
        try
        {
            long totalRamMb = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
            return ScaleHostMemoryBudgetMb(totalRamMb);
        }
        catch
        {
            return HostMemoryBudgetFloorMb;
        }
    }

    /// <summary>
    /// Idle sessions released within this window are treated as part of the active pipeline
    /// working set and are only evicted when no older idle entry exists.
    /// </summary>
    private const long RecentReleaseWindowMs = 120_000;

    private sealed class PoolEntry(InferenceSession session, bool ephemeral, AdmissionBucket bucket) : IDisposable
    {
        private long lastReleasedTicks = Environment.TickCount64;
        private volatile bool evicted;
        private int disposeState; // 0 = live, 1 = disposed; guarded by Interlocked for idempotency
        private int pinCount;

        public InferenceSession Session { get; } = session;

        /// <summary>
        /// Admission bucket the session's memory is accounted in: the bucket of the provider it
        /// was created with, which is not always the bucket of its key's provider.
        /// </summary>
        public AdmissionBucket Bucket { get; } = bucket;

        /// <summary>Per-entry gate that serialises access (one user at a time).</summary>
        public SemaphoreSlim Gate { get; } = new(0, 1); // Starts unavailable because the creator immediately owns the first lease.

        /// <summary>
        /// Residency pins (audit §3A). Pinned entries are not idle-evicted. Independent of
        /// <see cref="Gate"/> — a session can be resident and still free for execution.
        /// </summary>
        public int PinCount => Volatile.Read(ref pinCount);

        public bool TryPin()
        {
            if (evicted || disposeState != 0)
            {
                return false;
            }

            Interlocked.Increment(ref pinCount);
            if (evicted || disposeState != 0)
            {
                Unpin();
                return false;
            }

            return true;
        }

        public void Unpin()
        {
            if (Interlocked.Decrement(ref pinCount) < 0)
            {
                Interlocked.Exchange(ref pinCount, 0);
            }
        }

        /// <summary>
        /// When <see langword="true"/> the entry was created beyond the pool limit and will not
        /// be stored; its session is disposed when the lease is released.
        /// </summary>
        public bool Ephemeral { get; private set; } = ephemeral;

        /// <summary>
        /// Heuristic snapshot: <see langword="true"/> when the gate appears idle at the moment of
        /// reading.  <em>Not</em> safe as a disposal guard — a concurrent <see cref="SemaphoreSlim.WaitAsync()"/>
        /// can race between the read and a subsequent dispose.  Eviction paths must use
        /// <c>Gate.Wait(0)</c> to atomically acquire the gate before disposing.
        /// </summary>
        public bool IsIdle => Gate.CurrentCount == 1;

        /// <summary>
        /// <see langword="true"/> once the entry has been removed from the pool by an eviction path.
        /// The lease holder is responsible for disposing the entry when it releases.
        /// </summary>
        public bool IsEvicted => evicted;

        /// <summary>Tick count at last <c>Release()</c> call — used for LRU eviction ordering.</summary>
        public long LastReleasedTicks => Volatile.Read(ref lastReleasedTicks);

        /// <summary>
        /// Marks this entry as evicted so that its lease holder disposes it on release rather than
        /// returning it to the pool.  Called under <c>creationLock</c> by eviction code.
        /// </summary>
        public void MarkEvicted() => evicted = true;

        public void MarkEphemeral() => Ephemeral = true;

        public void Release()
        {
            Volatile.Write(ref lastReleasedTicks, Environment.TickCount64);
            Gate.Release();
        }

        public void ReleaseGateWithoutTouchingLru() => Gate.Release();

        /// <summary>
        /// Idempotent dispose: safe to call from both the eviction path and the lease-release path
        /// concurrently without double-disposing the underlying session or semaphore.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeState, 1) == 0)
            {
                Gate.Dispose();
                // Healthy sessions always release their native memory. Only a TensorRT-RTX session
                // in a process whose runtime was poisoned by a CUDA OOM skips teardown (see
                // TensorRtRtxTeardownGuard); dispose of such a session can fault uncatchably.
                TensorRtRtxTeardownGuard.DisposeSafely(Session);
            }
        }
    }

    private readonly int maxSessions;
    private readonly ConcurrentDictionary<SessionPoolKey, PoolEntry> entries = new();
    private readonly ConcurrentDictionary<SessionPoolKey, SemaphoreSlim> createGates = new();
    /// <summary>
    /// Most recent construction failure per key. A waiter that wakes to find no entry and no
    /// in-flight creation consumes this so a failed creator's exception propagates without
    /// re-invoking the factory (single-flight: one factory call per creation wave).
    /// </summary>
    /// <summary>
    /// Monotonic creation-wave id. Incremented only when a factory fails and the failure is
    /// recorded; waiters capture the observed wave before queueing so only failures from a
    /// later wave (i.e. a creator they actually waited behind) are propagated.
    /// </summary>
    private long creationWave;
    private readonly ConcurrentDictionary<SessionPoolKey, (Exception Error, long Wave)> creationFailures = new();
    private readonly SemaphoreSlim creationLock = new(1, 1);
    private readonly SemaphoreSlim bundleAcquireLock = new(1, 1);
    private TaskCompletionSource<bool> bundleStateChanged = CreateBundleStateChangedSignal();
    private readonly bool enableMemoryAdmission;
    private readonly long memoryBudgetMb;
    private readonly long hostMemoryBudgetMb;
    /// <summary>
    /// Late-bound source of this process's dedicated GPU reading, resolved once per accelerator
    /// admission decision. <see langword="null"/> when the host registered no reader, in which
    /// case admission accounts for the pool's own reservations only.
    /// </summary>
    private readonly Func<IProcessGpuMemoryReader?>? processGpuMemoryReader;
    /// <summary>Pending create reservations per admission bucket (host RAM vs accelerator device).</summary>
    private readonly ConcurrentDictionary<AdmissionBucket, long> pendingCreateMbByBucket = new();
    /// <summary>Live external-residency reservations (e.g. GenAI model loads) per bucket.</summary>
    private readonly ConcurrentDictionary<Guid, ExternalReservationState> externalReservations = new();
    private int admissionWaiters;
    private volatile bool disposed;
    private int pooledCount;
    private int disposeOnce; // 0 = not yet, 1 = disposed; Interlocked guard for single-winner teardown

    /// <summary>
    /// Admission accounting bucket: CPU/DNNL and OpenVINO CPU-proxy sessions share host RAM;
    /// standalone OpenVINO accelerator sessions use an NPU-specific bucket, separate from GPU 0.
    /// </summary>
    private readonly record struct AdmissionBucket(bool IsHost, ExecutionProviderKind? AcceleratorProvider, int DeviceId);

    private static bool IsHostProvider(ExecutionProviderKind provider) =>
        provider is ExecutionProviderKind.Cpu or ExecutionProviderKind.Dnnl;

    private static AdmissionBucket BucketOf(SessionPoolKey key) =>
        IsHostProvider(key.Provider) || (key.Provider is ExecutionProviderKind.OpenVino && key.UseOpenVinoCpuProxy)
            ? new(true, null, 0)
            : key.Provider is ExecutionProviderKind.OpenVino
                ? new(false, ExecutionProviderKind.OpenVino, key.DeviceId ?? 0)
                : new(false, null, key.DeviceId ?? 0);

    private static AdmissionBucket BucketOf(
        ExecutionProviderKind provider,
        int? deviceId,
        bool useOpenVinoCpuProxy = false) =>
        IsHostProvider(provider) || (provider is ExecutionProviderKind.OpenVino && useOpenVinoCpuProxy)
            ? new(true, null, 0)
            : provider is ExecutionProviderKind.OpenVino
                ? new(false, ExecutionProviderKind.OpenVino, deviceId ?? 0)
                : new(false, null, deviceId ?? 0);

    private static AdmissionBucket BucketOf(SessionPoolKey key, ExecutionProviderKind createdProvider) =>
        createdProvider == key.Provider
            ? BucketOf(key)
            : BucketOf(createdProvider, key.DeviceId, key.UseOpenVinoCpuProxy);

    private long BudgetFor(AdmissionBucket bucket) =>
        bucket.IsHost ? hostMemoryBudgetMb : memoryBudgetMb;

    private static string DescribeBucket(AdmissionBucket bucket) =>
        bucket.IsHost
            ? "host RAM"
            : bucket.AcceleratorProvider is ExecutionProviderKind.OpenVino
                ? $"OpenVINO NPU device {bucket.DeviceId}"
                : $"accelerator device {bucket.DeviceId}";

    /// <summary>
    /// Live state for one external memory reservation. The pool owns accounting; the
    /// <see cref="ExternalMemoryReservation"/> token is only a handle into this entry.
    /// <see cref="TryEvictIdle"/> is registered by the owner of the external resource and
    /// returns <see langword="true"/> only when it actually released the idle resource.
    /// </summary>
    private sealed class ExternalReservationState(
        AdmissionBucket bucket,
        long estimatedMemoryMb)
    {
        public AdmissionBucket Bucket { get; } = bucket;
        public long EstimatedMemoryMb { get; } = estimatedMemoryMb;
        // volatile: the callback is registered after the state is already visible in the
        // dictionary, so readers must see the write without a lock.
        public volatile Func<bool>? TryEvictIdle;
        public long LastReleasedTicks { get; set; } = Environment.TickCount64;
    }

    public InferenceSessionPool(
        int maxSessions = DefaultMaxSessions,
        bool enableMemoryAdmission = true,
        long? memoryBudgetMb = null,
        long? hostMemoryBudgetMb = null,
        Func<IProcessGpuMemoryReader?>? processGpuMemoryReader = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSessions, 1);
        long resolvedMemoryBudgetMb = memoryBudgetMb ?? DefaultMemoryBudgetMb;
        ArgumentOutOfRangeException.ThrowIfLessThan(resolvedMemoryBudgetMb, 1);
        long resolvedHostBudgetMb = hostMemoryBudgetMb ?? DefaultHostMemoryBudgetMb;
        ArgumentOutOfRangeException.ThrowIfLessThan(resolvedHostBudgetMb, 1);
        this.maxSessions = maxSessions;
        this.enableMemoryAdmission = enableMemoryAdmission;
        this.memoryBudgetMb = resolvedMemoryBudgetMb;
        this.hostMemoryBudgetMb = resolvedHostBudgetMb;
        this.processGpuMemoryReader = processGpuMemoryReader;
    }

    private static TaskCompletionSource<bool> CreateBundleStateChangedSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Wakes bundle acquisition after a gate or pool membership changes. The exchange makes the
    /// signal one-shot: a waiter captures the current source before its attempt, while a later
    /// transition installs a fresh source for the next waiter.
    /// </summary>
    private void SignalBundleStateChanged()
    {
        TaskCompletionSource<bool> previous =
            Interlocked.Exchange(ref bundleStateChanged, CreateBundleStateChangedSignal());
        previous.TrySetResult(true);
    }

    /// <summary>
    /// Returns an appropriate default max-session count for the current hardware.
    /// On Windows with a discrete NVIDIA GPU and >= 8 GB VRAM, returns 24 (covers a full
    /// multi-graph pipeline without mid-run LRU thrash). Otherwise returns 12.
    /// </summary>
    public static int RecommendedMaxSessions(IReadOnlyList<DeviceEntry>? devices)
    {
        if (devices is null)
            return DefaultMaxSessions;

        DeviceEntry? nvidiaGpu = devices.FirstOrDefault(d =>
            d.Kind == DeviceKind.DiscreteGpu &&
            string.Equals(d.VendorName, "NVIDIA", StringComparison.OrdinalIgnoreCase));

        if (nvidiaGpu is not null && nvidiaGpu.DedicatedVramMb >= 8192)
            return 24;

        return DefaultMaxSessions;
    }

    /// <summary>
    /// Pre-warms a session for <paramref name="key"/> without returning a lease.
    /// Useful at startup to amortise first-call latency.
    /// If a session for this key already exists the call is a no-op.
    /// The session is resident but <em>unpinned</em> and may be idle-evicted later.
    /// Use <see cref="GetResidencyAsync"/> when it must stay warm.
    /// </summary>
    public async Task WarmAsync(
        SessionPoolKey key,
        Func<CancellationToken, Task<InferenceSession>> factory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        using SessionLease lease = await GetLeaseAsync(key, factory, cancellationToken).ConfigureAwait(false);
        // Lease is immediately released on exit — session stays in the pool.
    }

    /// <summary>
    /// Creates or reuses <paramref name="key"/> and returns a <see cref="SessionResidency"/> pin.
    /// The pin keeps the session out of idle eviction but does <em>not</em> hold the execution
    /// gate — other callers can still <see cref="GetLeaseAsync"/> and Run() (audit §3A:
    /// residency ≠ exclusivity). Dispose the residency to allow eviction again.
    /// </summary>
    public async Task<SessionResidency> GetResidencyAsync(
        SessionPoolKey key,
        Func<CancellationToken, Task<InferenceSession>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        return await GetResidencyReportingProviderAsync(
            key,
            async ct => new CreatedPoolSession(await factory(ct).ConfigureAwait(false), key.Provider),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="GetResidencyAsync"/> for a factory that reports the provider the session was
    /// actually created on (see <see cref="GetLeaseReportingProviderAsync"/>).
    /// </summary>
    internal async Task<SessionResidency> GetResidencyReportingProviderAsync(
        SessionPoolKey key,
        Func<CancellationToken, Task<CreatedPoolSession>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        while (true)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            // Ensure the session exists (single-flight create). Release the lease immediately.
            using (await GetLeaseReportingProviderAsync(key, factory, cancellationToken).ConfigureAwait(false))
            {
            }

            if (entries.TryGetValue(key, out PoolEntry? entry) && entry.TryPin())
            {
                PoolEntry pinned = entry;
                return new SessionResidency(pinned.Unpin);
            }

            // Evicted between release and pin — retry create.
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Reserves <paramref name="estimatedMemoryMb"/> against the admission bucket for
    /// <paramref name="provider"/>/<paramref name="deviceId"/> and returns a token whose
    /// disposal releases the accounting. Used by external residency owners (e.g. ORT GenAI
    /// model caches) so model resources share the same host RAM / per-device accelerator
    /// ceiling as pooled ONNX sessions instead of an independent cache.
    /// </summary>
    /// <remarks>
    /// External reservations always use hard admission — they cannot opt into ephemeral
    /// overflow — and one token occupies exactly one bucket: CPU/DNNL/OpenVINO share the
    /// host RAM budget when OpenVINO CPU-proxy mode is active; otherwise OpenVINO and every
    /// other accelerator provider use their device budget.
    /// </remarks>
    internal async Task<ExternalMemoryReservation> ReserveExternalAsync(
        ExecutionProviderKind provider,
        int? deviceId,
        long estimatedMemoryMb,
        CancellationToken cancellationToken,
        bool useOpenVinoCpuProxy = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(estimatedMemoryMb, 1);
        ObjectDisposedException.ThrowIf(disposed, this);

        AdmissionBucket bucket = BucketOf(provider, deviceId, useOpenVinoCpuProxy);
        long budgetMb = BudgetFor(bucket);
        if (estimatedMemoryMb > budgetMb)
        {
            throw new InvalidOperationException(
                $"External resource for '{provider}' needs ~{estimatedMemoryMb} MB, which exceeds the " +
                $"{DescribeBucket(bucket)} admission budget of {budgetMb} MB.");
        }

        // Takes the pending reservation (evicting idle pooled/external entries to fit) so
        // benchmark maxima see external construction honestly; publishing the live state
        // below then transfers the accounting so nothing is double-counted.
        await WaitForAdmissionBudgetAsync(estimatedMemoryMb, bucket, cancellationToken).ConfigureAwait(false);

        // Publish under creationLock so pool disposal cannot interleave: either the reserve
        // wins the lock and publishes first (Dispose then sees and clears the state), or
        // Dispose wins and the publish throws ObjectDisposedException before accounting
        // for a resource the pool can no longer track.
        Guid id = Guid.NewGuid();
        bool published = false;
        bool pendingReleased = false;
        try
        {
            await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                externalReservations[id] = new ExternalReservationState(bucket, estimatedMemoryMb);
                // Transfer pending -> live while holding creationLock so admission readers
                // cannot observe the same reservation in both accounting buckets.
                ReleaseReservation(bucket, estimatedMemoryMb);
                pendingReleased = true;
                published = true;
            }
            finally
            {
                creationLock.Release();
            }

            SignalBundleStateChanged();
            return new ExternalMemoryReservation(this, id);
        }
        finally
        {
            // Release pending accounting on cancellation, disposal, or publish failure.
            if (!pendingReleased)
            {
                ReleaseReservation(bucket, estimatedMemoryMb);
            }
            if (!published)
            {
                externalReservations.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// Attaches the idle-eviction callback to a still-live reservation. Returns
    /// <see langword="false"/> when the pool is disposed or the id is already gone — callers
    /// must treat false as "the reservation no longer exists" and roll back rather than
    /// publish an unaccounted resource.
    /// </summary>
    internal bool TrySetExternalEvictionCallback(Guid id, Func<bool> callback)
    {
        creationLock.Wait();
        try
        {
            if (disposed
                || !externalReservations.TryGetValue(id, out ExternalReservationState? state))
            {
                return false;
            }

            state.TryEvictIdle = callback;
            return true;
        }
        finally
        {
            creationLock.Release();
        }
    }

    internal void NotifyExternalReservationReleased(Guid id)
    {
        if (externalReservations.TryGetValue(id, out ExternalReservationState? state))
        {
            state.LastReleasedTicks = Environment.TickCount64;
            SignalBundleStateChanged();
        }
    }

    internal void ReleaseExternalReservation(Guid id)
    {
        if (externalReservations.TryRemove(id, out _))
        {
            SignalBundleStateChanged();
        }
    }

    /// <summary>
    /// Pins an already-pooled session if present. Used after a create/warm when the caller
    /// re-acquires with the same <see cref="SessionPoolKey"/> for each execution.
    /// </summary>
    public bool TryPinExisting(SessionPoolKey key, out SessionResidency? residency)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (entries.TryGetValue(key, out PoolEntry? entry) && entry.TryPin())
        {
            PoolEntry pinned = entry;
            residency = new SessionResidency(pinned.Unpin);
            return true;
        }

        residency = null;
        return false;
    }

    /// <summary>
    /// Returns an exclusive <see cref="SessionLease"/> for <paramref name="key"/>.
    /// If no session exists for the key, one is created using <paramref name="factory"/>
    /// (single-flight: concurrent misses share one factory call).
    /// When memory admission is enabled the create waits for budget after evicting idle
    /// sessions and never allocates an unbudgeted ephemeral session. When admission is off
    /// and the pool is full with no idle eviction target, the session is created outside
    /// the pool (ephemeral) and disposed when the lease is released.
    /// </summary>
    public async Task<SessionLease> GetLeaseAsync(
        SessionPoolKey key,
        Func<CancellationToken, Task<InferenceSession>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        return await GetLeaseReportingProviderAsync(
            key,
            async ct => new CreatedPoolSession(await factory(ct).ConfigureAwait(false), key.Provider),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="GetLeaseAsync"/> for a factory whose provider fallback can create the session
    /// on a different provider than <see cref="SessionPoolKey.Provider"/>. The key is admitted
    /// against its own provider's bucket before the factory runs; when the session lands in
    /// another bucket (TensorRT RTX falling back to CPU moves it from the device budget to host
    /// RAM), the pool releases the first reservation and re-admits the session against the
    /// bucket it was created in before publishing it, under the same rules as a direct request
    /// there: an estimate over that budget fails, otherwise idle sessions in the bucket are
    /// evicted to fit or the create waits. The published entry stays accounted, and evictable,
    /// in that bucket. Single-flight creation and lease semantics are unchanged.
    /// </summary>
    public async Task<SessionLease> GetLeaseReportingProviderAsync(
        SessionPoolKey key,
        Func<CancellationToken, Task<CreatedPoolSession>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        bool cacheMissRecorded = false;

        // Snapshot the creation wave at arrival: a caller that arrives while a creator is
        // in flight — but is delayed by a gate or lock until after that creator fails —
        // must still consume the recorded failure instead of re-running the factory.
        SemaphoreSlim createGate = createGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        long observedCreationWave = Volatile.Read(ref creationWave);
        bool creationSnapshotPending = true;

        while (true)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            // Fast path: entry already exists — wait on its gate (serialises inference).
            if (entries.TryGetValue(key, out PoolEntry? existing))
            {
                try
                {
                    using (BenchmarkPhaseCapture.Start("pool-wait"))
                        await existing.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (disposed)
                    {
                        ReleaseLeaseEntry(existing);
                        ObjectDisposedException.ThrowIf(disposed, this);
                    }

                    // One counter per acquire request: a request that already recorded
                    // a miss is not reclassified as a hit if it lands on an entry later.
                    if (!cacheMissRecorded)
                    {
                        BenchmarkPhaseCapture.Increment("poolHit");
                    }
                    return BuildLease(existing);
                }
                catch (ObjectDisposedException)
                {
                    // The entry was evicted and its SemaphoreSlim disposed between the dictionary
                    // lookup and WaitAsync. Retry from the top so we can observe the current pool
                    // state (including full-pool disposal) before deciding whether to create.
                    continue;
                }
            }

            if (!cacheMissRecorded)
            {
                BenchmarkPhaseCapture.Increment("poolMiss");
                cacheMissRecorded = true;
            }

            // Single-flight create: one factory invocation per key. Cancelling this waiter
            // does not cancel the shared create for other callers. A caller that queues behind
            // an in-flight creator (gate already held) consumes that creator's recorded
            // failure so construction failures propagate without rebuilding; callers that
            // arrive afterwards retry the factory — failed sessions are not cached.
            if (!creationSnapshotPending)
            {
                // A retry iteration re-observes the pool after the arrival snapshot was
                // consumed — refresh it so each iteration reflects current state.
                createGate = createGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
                observedCreationWave = Volatile.Read(ref creationWave);
            }
            creationSnapshotPending = false;

            bool propagatedRecentFailure = false;
            using (BenchmarkPhaseCapture.Start("pool-single-flight-wait"))
                await createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (entries.ContainsKey(key))
                {
                    continue;
                }

                // If memory admission is enabled and model exceeds budget, fail fast before
                // attempting to create an unbudgeted session (audit §3B).
                long needMb = ResolveReservationMb(key);
                AdmissionBucket bucket = BucketOf(key);
                if (enableMemoryAdmission && needMb > BudgetFor(bucket))
                {
                    throw new InvalidOperationException(
                        $"'{key.EngineFamily}' needs ~{needMb} MB, which exceeds the " +
                        $"{DescribeBucket(bucket)} admission budget of {BudgetFor(bucket)} MB.");
                }

                if (creationFailures.TryGetValue(key, out var recentFailure))
                {
                    if (recentFailure.Wave > observedCreationWave)
                    {
                        // Failure from a later wave than our arrival snapshot: we arrived
                        // while that creator was in flight — propagate without rebuilding.
                        propagatedRecentFailure = true;
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(recentFailure.Error).Throw();
                    }

                    // Stale failure from an earlier wave: retry the factory.
                    creationFailures.TryRemove(new KeyValuePair<SessionPoolKey, (Exception Error, long Wave)>(key, recentFailure));
                }

                bool ephemeral = false;
                bool reserved = false;
                // Observe outside creationLock: the first Windows performance-counter read can
                // block for around a second on a cold host, and it must not stall other creators.
                // Host-RAM buckets are never charged with the observation, so they skip the
                // probe instead of polling GPU counters on every admission.
                ProcessGpuObservation? observation = enableMemoryAdmission && !bucket.IsHost
                    ? ReadObservedProcessGpu()
                    : null;
                using (BenchmarkPhaseCapture.Start("pool-creation-lock-wait"))
                    await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (entries.ContainsKey(key))
                    {
                        // No reservation is held yet at this point (reserved is only set below).
                        continue;
                    }

                    if (enableMemoryAdmission)
                    {
                        reserved = TryReserveAdmissionBudget(bucket, needMb, observation);
                        // else: do not hold a reservation while waiting — that deadlocks
                        // when every waiter reserves and nobody can release.
                    }
                    // else (count mode): do not evict or decide ephemeral here — the entry we'd
                    // evict is still usable and factory() has not succeeded yet. The lruEvicted2
                    // path after a successful create handles capacity instead.
                }
                finally
                {
                    creationLock.Release();
                }

                if (enableMemoryAdmission && !reserved)
                {
                    await WaitForAdmissionBudgetAsync(needMb, bucket, cancellationToken).ConfigureAwait(false);
                    // Budget reserved by WaitForAdmissionBudgetAsync on success.
                    reserved = true;
                }

                InferenceSession session;
                ExecutionProviderKind createdProvider;
                try
                {
                    BenchmarkPhaseCapture.Increment("sessionCreate");
                    CreatedPoolSession created;
                    using (BenchmarkPhaseCapture.Start("session-create"))
                        created = await factory(cancellationToken).ConfigureAwait(false);
                    session = created.Session;
                    createdProvider = created.Provider;
                    if (createdProvider is ExecutionProviderKind.TensorRTRtx)
                    {
                        TensorRtRtxTeardownGuard.Track(session);
                    }
                }
                catch (Exception ex)
                {
                    if (key.Provider is ExecutionProviderKind.TensorRTRtx)
                    {
                        TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(ex);
                    }

                    if (reserved)
                    {
                        // Always release reservation on factory failure to prevent a reservation leak
                        // (audit §3A: memory accounting integrity).
                        ReleaseReservation(bucket, needMb);
                    }

                    throw;
                }

                AdmissionBucket createdBucket = BucketOf(key, createdProvider);
                if (createdBucket != bucket)
                {
                    // The factory's provider fallback created the session outside the bucket its
                    // key was admitted against, so that bucket never holds this memory.
                    if (reserved)
                    {
                        ReleaseReservation(bucket, needMb);
                        reserved = false;
                    }

                    bucket = createdBucket;
                    if (enableMemoryAdmission)
                    {
                        try
                        {
                            await ReadmitCreatedSessionAsync(key, createdProvider, needMb, bucket, cancellationToken).ConfigureAwait(false);
                            reserved = true;
                        }
                        catch
                        {
                            TensorRtRtxTeardownGuard.DisposeSafely(session);
                            throw;
                        }
                    }
                }

                var freshEntry = new PoolEntry(session, ephemeral, bucket);

                if (freshEntry.Ephemeral)
                {
                    if (reserved)
                    {
                        ReleaseReservation(bucket, needMb);
                    }

                    return BuildLease(freshEntry);
                }

                bool published = false;
                PoolEntry? competitor = null;
                PoolEntry? lruEvicted2 = null;
                try
                {
                    await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        ObjectDisposedException.ThrowIf(disposed, this);

                        if (entries.TryGetValue(key, out competitor))
                        {
                            published = false;
                        }
                        else
                        {
                            if (!enableMemoryAdmission)
                            {
                                lruEvicted2 = pooledCount >= maxSessions ? TryEvictLruIdle() : null;
                                if (pooledCount >= maxSessions && lruEvicted2 is null)
                                {
                                    freshEntry.MarkEphemeral();
                                }
                            }

                            if (!freshEntry.Ephemeral)
                            {
                                published = entries.TryAdd(key, freshEntry);
                                if (!published)
                                {
                                    entries.TryGetValue(key, out competitor);
                                }
                                else
                                {
                                    pooledCount++;
                                    SignalBundleStateChanged();
                                }
                            }
                        }
                    }
                    finally
                    {
                        creationLock.Release();
                    }
                }
                catch
                {
                    if (!published)
                    {
                        freshEntry.Dispose();
                    }

                    if (reserved)
                    {
                        ReleaseReservation(bucket, needMb);
                    }

                    lruEvicted2?.Dispose();
                    throw;
                }

                lruEvicted2?.Dispose();

                if (published)
                {
                    // Estimate now lives on the pooled entry (CurrentReservedMb sums entries);
                    // drop the pending reservation so it is not double-counted.
                    if (reserved)
                    {
                        ReleaseReservation(bucket, needMb);
                    }

                    return BuildLease(freshEntry);
                }

                if (freshEntry.Ephemeral)
                {
                    if (reserved)
                    {
                        ReleaseReservation(bucket, needMb);
                    }

                    return BuildLease(freshEntry);
                }

                // Lost the race: another thread concurrently published an entry for this key.
                // Discard our duplicate session and lease the winner's entry instead.
                freshEntry.Dispose();
                if (reserved)
                {
                    ReleaseReservation(bucket, needMb);
                }

                if (competitor is not null)
                {
                    try
                    {
                        await competitor.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        if (disposed)
                        {
                            ReleaseLeaseEntry(competitor);
                            ObjectDisposedException.ThrowIf(disposed, this);
                        }

                        return BuildLease(competitor);
                    }
                    catch (ObjectDisposedException)
                    {
                        continue;
                    }
                }

                continue;
            }
            catch (Exception ex)
            {
                if (!propagatedRecentFailure && ex is not OperationCanceledException)
                {
                    creationFailures[key] = (ex, Interlocked.Increment(ref creationWave));
                }

                throw;
            }
            finally
            {
                createGate.Release();
            }
        }
    }

    /// <summary>
    /// Admits a session that already exists against <paramref name="bucket"/>, the bucket of the
    /// provider it was created with, and takes the pending reservation there. The session is not
    /// yet published, so it is never an eviction candidate for its own admission. When the bucket
    /// cannot fit it immediately, the created session is charged against the bucket before the
    /// wait: its memory is real and resident, so a wait must never hide it from the budget
    /// (concurrent CPU fallbacks for distinct keys would otherwise each park a full model
    /// unaccounted and silently exceed the host ceiling).
    /// </summary>
    private async Task ReadmitCreatedSessionAsync(
        SessionPoolKey key,
        ExecutionProviderKind createdProvider,
        long needMb,
        AdmissionBucket bucket,
        CancellationToken cancellationToken)
    {
        if (needMb > BudgetFor(bucket))
        {
            throw new InvalidOperationException(
                $"'{key.EngineFamily}' was admitted for {key.Provider} but its session was created on " +
                $"{createdProvider} and needs ~{needMb} MB, which exceeds the {DescribeBucket(bucket)} " +
                $"admission budget of {BudgetFor(bucket)} MB.");
        }

        ProcessGpuObservation? observation = bucket.IsHost ? null : ReadObservedProcessGpu();
        bool reserved;
        await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            reserved = TryReserveAdmissionBudget(bucket, needMb, observation);
        }
        finally
        {
            creationLock.Release();
        }

        if (!reserved)
        {
            // Charge the already-created session before waiting so the budget reflects its real
            // memory for the whole wait, then wait for the bucket total (including it) to fit.
            AddPendingReservation(bucket, needMb);
            try
            {
                await WaitForAdmissionBudgetAsync(needMb, bucket, cancellationToken, alreadyReserved: true)
                    .ConfigureAwait(false);
            }
            catch
            {
                ReleaseReservation(bucket, needMb);
                throw;
            }
        }
    }

    private long ResolveReservationMb(SessionPoolKey key) =>
        key.EstimatedVramMb > 0 ? key.EstimatedVramMb : SessionPoolKey.DefaultEstimatedVramMb;

    /// <summary>
    /// Validates that a bundle's aggregate estimate per admission bucket fits that bucket's
    /// budget. Buckets follow the provider each session was actually created with: a published
    /// entry carries its creation bucket (<see cref="PoolEntry.Bucket"/>), so a GPU-keyed CPU
    /// fallback is charged against host RAM rather than the accelerator bucket implied by its
    /// key. Missing entries are checked against their key's bucket. Fail-fast at this point
    /// (before any factory runs, and again after each residency pin) prevents a bundle from
    /// pinning a fallback session and then waiting forever on memory that its own pin holds.
    /// </summary>
    private void ValidateBundleAdmission(SessionLeaseRequest[] ordered)
    {
        foreach (IGrouping<AdmissionBucket, SessionLeaseRequest> group in
                 ordered.GroupBy(request =>
                     entries.TryGetValue(request.Key, out PoolEntry? entry) && entry is not null
                         ? entry.Bucket
                         : BucketOf(request.Key)))
        {
            long totalMb = group.Sum(request => ResolveReservationMb(request.Key));
            long budgetMb = BudgetFor(group.Key);
            if (totalMb > budgetMb)
            {
                throw new InvalidOperationException(
                    $"Session bundle needs ~{totalMb} MB across {group.Count()} graph(s) on " +
                    $"{DescribeBucket(group.Key)}, which exceeds its admission budget of {budgetMb} MB.");
            }
        }
    }

    /// <summary>
    /// Acquires every graph in <paramref name="requests"/> as one all-or-nothing bundle
    /// (audit §3A: never hold an encoder while waiting indefinitely for a decoder).
    /// Sessions are created and pinned resident first (serialized under
    /// <c>bundleAcquireLock</c> so opposing bundles cannot pin themselves into a memory
    /// deadlock), then exclusive gates are taken in <see cref="SessionPoolKey.StableComparer"/>
    /// order without blocking on a later key while holding an earlier one — if any gate is
    /// busy the attempt rolls back and retries. Duplicate keys are rejected. When memory
    /// admission is enabled, an aggregate per-bucket budget check fails the bundle before
    /// any factory runs.
    /// </summary>
    public async Task<SessionLeaseBundle> GetLeaseBundleAsync(
        IReadOnlyList<SessionLeaseRequest> requests,
        CancellationToken cancellationToken)
    {
        SessionLeaseRequest[] ordered = ValidateBundleRequests(requests);

        if (enableMemoryAdmission)
        {
            // Aggregate preflight: a bundle whose graphs jointly exceed a bucket's budget
            // can never fit, so fail before invoking any factory. Buckets follow the provider
            // each session was actually created with (a published CPU fallback is charged to
            // host RAM, not its GPU key's bucket), so the check is repeated after each
            // residency pin below — pinning a fallback session can change a bucket's total.
            ValidateBundleAdmission(ordered);
        }

        // Serialize preparation and acquisition: while one bundle is pinning or acquiring,
        // no opposing bundle can incrementally pin itself into a memory deadlock.
        await bundleAcquireLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pins = new List<SessionResidency>(ordered.Length);
        try
        {
            try
            {
                // Phase 1: create each session — no execution gates held. Under hard
                // admission, pin each graph resident so idle eviction cannot remove it
                // before its gate is taken. In count mode a warm may land ephemeral and
                // never reach `entries`, so a residency pin could spin forever — just
                // warm and release instead; the bounded re-warm below retries.
                foreach (SessionLeaseRequest request in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (enableMemoryAdmission)
                    {
                        pins.Add(await GetResidencyReportingProviderAsync(request.Key, request.CreateReportingProviderAsync, cancellationToken)
                            .ConfigureAwait(false));
                        // Recheck after each pin: a session pinned now is counted in the bucket
                        // it was created in, and a later admission must not wait on memory this
                        // bundle's own pin holds.
                        ValidateBundleAdmission(ordered);
                    }
                    else
                    {
                        using SessionLease warm = await GetLeaseReportingProviderAsync(request.Key, request.CreateReportingProviderAsync, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                int rewarmAttempts = 0;
                const int maxRewarmAttempts = 25; // ~5s of real re-warm tries at the 200ms cadence below.
                var rewarmStopwatch = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    cancellationToken.ThrowIfCancellationRequested();

                    // Capture the signal before attempting the bundle. A lease release or pool
                    // publication that races this attempt either wakes this waiter or is observed
                    // by the next iteration, so no state change can be lost between check and wait.
                    Task bundleStateSignal = Volatile.Read(ref bundleStateChanged).Task;
                    SessionLeaseBundle? bundle = TryAcquireBundle(ordered, requests);
                    if (bundle is not null)
                    {
                        // Execution gates now protect the entries; the residency pins have
                        // done their job.
                        foreach (SessionResidency pin in pins)
                        {
                            pin.Dispose();
                        }

                        pins.Clear();
                        return bundle;
                    }

                    // A key can still be missing from `entries` — a pinned entry survives idle
                    // eviction, but explicit EvictAllIdleAsync/EvictModelAsync calls remove it.
                    // Re-warm on a throttled cadence (~200ms) gated by elapsed wall-clock time
                    // rather than iteration count, as before.
                    if (rewarmStopwatch.Elapsed >= TimeSpan.FromMilliseconds(200))
                    {
                        rewarmStopwatch.Restart();
                        SessionLeaseRequest[] missing =
                            ordered.Where(request => !entries.ContainsKey(request.Key)).ToArray();
                        if (missing.Length > 0)
                        {
                            foreach (SessionLeaseRequest request in missing)
                            {
                                if (enableMemoryAdmission)
                                {
                                    pins.Add(await GetResidencyReportingProviderAsync(request.Key, request.CreateReportingProviderAsync, cancellationToken)
                                        .ConfigureAwait(false));
                                    ValidateBundleAdmission(ordered);
                                }
                                else
                                {
                                    using SessionLease warm = await GetLeaseReportingProviderAsync(request.Key, request.CreateReportingProviderAsync, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                        }

                        // The pool stays at capacity (every entry leased or pinned) if a re-warm
                        // still leaves a key unpooled. Bound the retries instead of spinning until
                        // the caller's cancellation token fires while holding bundleAcquireLock.
                        if (missing.Length > 0 &&
                            ordered.Any(request => !entries.ContainsKey(request.Key)))
                        {
                            rewarmAttempts++;
                            if (rewarmAttempts >= maxRewarmAttempts)
                            {
                                throw new InvalidOperationException(
                                    "Unable to acquire session bundle: the pool stayed at capacity " +
                                    $"(every entry leased or pinned) across {maxRewarmAttempts} re-warm attempts.");
                            }
                        }
                    }

                    // Wait for a state transition rather than polling every 10ms. The timeout is
                    // only a lost-wakeup safety net; normal progress is signaled by release,
                    // publication, and eviction paths below. Kept at the same ~200ms cadence as the
                    // re-warm gate above so the stuck-pool give-up bound stays near the documented
                    // ~5s (maxRewarmAttempts * 200ms) even when no signal ever fires.
                    try
                    {
                        await bundleStateSignal.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Lost-wakeup safety net; retry the bundle attempt.
                    }
                }
            }
            finally
            {
                foreach (SessionResidency pin in pins)
                {
                    pin.Dispose();
                }
            }
        }
        finally
        {
            bundleAcquireLock.Release();
        }
    }

    private SessionLeaseBundle? TryAcquireBundle(
        SessionLeaseRequest[] ordered,
        IReadOnlyList<SessionLeaseRequest> requests)
    {
        var held = new List<(SessionPoolKey Key, PoolEntry Entry)>(ordered.Length);
        bool allAcquired = true;
        foreach (SessionLeaseRequest request in ordered)
        {
            if (!entries.TryGetValue(request.Key, out PoolEntry? entry) || !TryAcquireGate(entry))
            {
                allAcquired = false;
                break;
            }

            if (entry.IsEvicted)
            {
                entry.ReleaseGateWithoutTouchingLru();
                allAcquired = false;
                break;
            }

            held.Add((request.Key, entry));
        }

        if (allAcquired && held.Count == ordered.Length)
        {
            return BuildBundleInRequestOrder(ordered, requests, held);
        }

        foreach ((SessionPoolKey _, PoolEntry entry) in held)
        {
            entry.ReleaseGateWithoutTouchingLru();
        }

        return null;
    }

    private SessionLeaseBundle BuildBundleInRequestOrder(
        SessionLeaseRequest[] ordered,
        IReadOnlyList<SessionLeaseRequest> requests,
        List<(SessionPoolKey Key, PoolEntry Entry)> held)
    {
        var leases = new SessionLease[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            SessionPoolKey key = ordered[i].Key;
            int requestIndex = -1;
            for (int r = 0; r < requests.Count; r++)
            {
                if (Equals(requests[r].Key, key))
                {
                    requestIndex = r;
                    break;
                }
            }

            PoolEntry entry = held.First(h => Equals(h.Key, key)).Entry;
            leases[requestIndex] = BuildLease(entry);
        }

        return new SessionLeaseBundle(leases);
    }

    private static SessionLeaseRequest[] ValidateBundleRequests(IReadOnlyList<SessionLeaseRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("Bundle requires at least one session.", nameof(requests));
        }

        SessionLeaseRequest[] ordered = requests.OrderBy(r => r.Key, SessionPoolKey.StableComparer).ToArray();
        for (int i = 1; i < ordered.Length; i++)
        {
            if (Equals(ordered[i - 1].Key, ordered[i].Key))
            {
                throw new ArgumentException(
                    "Bundle contains duplicate session keys; multi-graph bundles require distinct graphs.",
                    nameof(requests));
            }
        }

        return ordered;
    }

    private long CurrentReservedMb(AdmissionBucket bucket)
    {
        long pooled = 0;
        foreach (KeyValuePair<SessionPoolKey, PoolEntry> pair in entries.Where(pair => pair.Value.Bucket == bucket))
        {
            pooled += ResolveReservationMb(pair.Key);
        }

        long external = 0;
        foreach (KeyValuePair<Guid, ExternalReservationState> pair in externalReservations)
        {
            if (pair.Value.Bucket == bucket)
            {
                external += pair.Value.EstimatedMemoryMb;
            }
        }

        pendingCreateMbByBucket.TryGetValue(bucket, out long pending);
        return pooled + pending + external;
    }

    private const long BytesPerMegabyte = 1024L * 1024L;

    /// <summary>
    /// One observation of this process's dedicated GPU footprint for a single admission
    /// decision: the process total plus the per-adapter breakdown when the reader can
    /// attribute it. <see langword="null"/> whenever the reading cannot inform admission: no
    /// host registered a reader, the operator opted out, this platform or driver cannot report
    /// it (a GPU-idle process publishes no counter instance at all), or the probe failed. A
    /// null observation leaves reservation accounting exactly as it was.
    /// </summary>
    private sealed record ProcessGpuObservation(long TotalMb, IReadOnlyDictionary<long, long>? ByAdapterLuidMb);

    private ProcessGpuObservation? ReadObservedProcessGpu()
    {
        if (!SharedPoolOptions.EnableProcessGpuAdmission)
        {
            return null;
        }

        try
        {
            IProcessGpuMemoryReader? reader = processGpuMemoryReader?.Invoke();
            if (reader is null)
            {
                return null;
            }

            (long? bytes, IReadOnlyDictionary<long, long>? byLuid) = reader.ReadDedicatedGpuMemory();
            if (bytes is not > 0)
            {
                return null;
            }

            long totalMb = bytes.Value / BytesPerMegabyte;
            Dictionary<long, long>? byLuidMb = null;
            if (byLuid is not null)
            {
                byLuidMb = new Dictionary<long, long>(byLuid.Count);
                foreach ((long luid, long luidBytes) in byLuid)
                {
                    if (luidBytes > 0)
                    {
                        byLuidMb[luid] = luidBytes / BytesPerMegabyte;
                    }
                }
            }

            BenchmarkPhaseCapture.ObserveMaximum("observedProcessGpuMb", totalMb);
            return new ProcessGpuObservation(totalMb, byLuidMb);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failing probe (missing pdh.dll, a driver without the counter set, denied access)
            // or a mis-wired source must degrade the observation, never the admission path: an
            // unobserved pool keeps its reservation-only behaviour.
            return null;
        }
    }

    /// <summary>
    /// This process's dedicated GPU usage attributed to <paramref name="bucket"/>, in MB, or 0
    /// when there is nothing to attribute.
    /// </summary>
    /// <remarks>
    /// When the reader attributes usage per adapter and the host mapped this device to its
    /// adapter LUID, the bucket is charged exactly its own adapter's footprint: usage on other
    /// adapters never blocks it, and sibling reservations are never subtracted twice. Otherwise
    /// the full process total is the conservative upper bound for the deciding GPU. Sibling
    /// reservations are estimates, not measured allocations: subtracting them could hide real
    /// usage on this adapter. Host and NPU buckets are never charged with GPU observations.
    /// </remarks>
    private long ObservedProcessGpuUsageMb(AdmissionBucket bucket, ProcessGpuObservation? observation)
    {
        if (bucket.IsHost || bucket.AcceleratorProvider is not null || observation is null)
        {
            return 0;
        }

        if (observation.ByAdapterLuidMb is not null
            && SharedPoolOptions.AdapterLuidMap is { } map
            && map.TryGetValue(bucket.DeviceId, out long adapterLuid))
        {
            // The breakdown is complete when present: no entry means nothing on this adapter.
            return observation.ByAdapterLuidMb.TryGetValue(adapterLuid, out long deviceMb) ? deviceMb : 0;
        }

        return observation.TotalMb;
    }

    /// <summary>
    /// Admission usage for <paramref name="bucket"/>: the pool's committed reservations, floored
    /// by this process's real dedicated GPU usage for accelerator buckets. The floor is what makes
    /// the process-isolated reading — and accelerator memory held outside the pool — part of the
    /// admission decision rather than telemetry only.
    /// </summary>
    private long AdmissionUsageMb(AdmissionBucket bucket, ProcessGpuObservation? observation)
    {
        // In-flight creates hold a pending reservation but have not allocated yet, so the
        // process reading cannot contain them: charge them on top of the observed floor.
        // Otherwise a pending reservation disappears into the observation whenever the
        // observation is the binding term, and concurrent admissions overshoot the budget.
        pendingCreateMbByBucket.TryGetValue(bucket, out long pendingMb);
        return Math.Max(
            CurrentReservedMb(bucket),
            ObservedProcessGpuUsageMb(bucket, observation) + pendingMb);
    }

    private void AddPendingReservation(AdmissionBucket bucket, long mb)
    {
        pendingCreateMbByBucket.AddOrUpdate(bucket, mb, (_, existing) => existing + mb);
        // Process-wide maximum: host pending plus every accelerator bucket's pending.
        BenchmarkPhaseCapture.ObserveMaximum(
            "pendingReservationMb",
            pendingCreateMbByBucket.Values.Sum());
    }

    private void ReleaseReservation(AdmissionBucket bucket, long mb) =>
        pendingCreateMbByBucket.AddOrUpdate(bucket, 0, (_, existing) => Math.Max(0, existing - mb));

    /// <summary>
    /// Whether <paramref name="bucket"/> holds any pooled session (idle or leased) or live
    /// external reservation that could still turn over. A leased session becomes evictable on
    /// release, so its presence means an observation-held wait can still make progress.
    /// </summary>
    private bool BucketHasLiveEntries(AdmissionBucket bucket)
    {
        foreach (KeyValuePair<SessionPoolKey, PoolEntry> pair in entries)
        {
            if (pair.Value.Bucket == bucket)
            {
                return true;
            }
        }

        foreach (ExternalReservationState state in externalReservations.Values)
        {
            if (state.Bucket == bucket)
            {
                return true;
            }
        }

        // An in-flight create holds a pending reservation whose factory will turn over (publish
        // an entry or release the reservation), so it is live work too: the busy bound must
        // apply while it runs, or a slow factory in an otherwise-empty bucket trips the short
        // fail-fast before the pending drains from the observation.
        return pendingCreateMbByBucket.TryGetValue(bucket, out long pendingMb) && pendingMb > 0;
    }

    /// <summary>
    /// Evicts idle sessions in <paramref name="bucket"/> until <paramref name="needMb"/> fits,
    /// then takes the pending reservation. Eviction is driven by reservations only: evicting a
    /// pooled entry cannot lower the process-GPU observation snapshot, so an observation-blocked
    /// admission is left to <see cref="WaitForAdmissionBudgetAsync"/>, which re-observes while
    /// the released sessions' memory drains.
    /// </summary>
    /// <returns><see langword="true"/> when the reservation was taken.</returns>
    private bool TryReserveAdmissionBudget(AdmissionBucket bucket, long needMb, ProcessGpuObservation? observation)
    {
        // Other buckets have their own budgets (host RAM vs each accelerator).
        while (CurrentReservedMb(bucket) + needMb > BudgetFor(bucket))
        {
            PoolEntry? evictedForBudget = TryEvictLruIdle(onlyBucket: bucket);
            if (evictedForBudget is null)
            {
                break;
            }

            evictedForBudget.Dispose();
        }

        if (AdmissionUsageMb(bucket, observation) + needMb <= BudgetFor(bucket))
        {
            AddPendingReservation(bucket, needMb);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Consecutive wait passes blocked by the process-GPU observation alone — with no evictable
    /// pooled or external work left — before admission fails fast instead of parking until the
    /// caller cancels. Each pass waits ~50 ms, so the default bounds the stall at ~5 s. Mutable
    /// (rather than const) so tests can pin the fail-fast without waiting out the production
    /// bound.
    /// </summary>
    internal static int ObservedBlockFailFastPasses { get; set; } = 100;

    /// <summary>
    /// Same bound for passes where the bucket still holds live-but-unevictable entries (held
    /// leases, pinned sessions, live external reservations): their turnover can still make
    /// progress — a released lease becomes evictable — so they get a longer stall (~60 s)
    /// before the same fail-fast. A lease held for a whole stage, a nested acquire by the
    /// waiting caller itself, or a resident external without an eviction callback never turns
    /// over while this waiter blocks, so the wait stays bounded for those too.
    /// </summary>
    internal static int ObservedBlockBusyFailFastPasses { get; set; } = 1200;

    /// <summary>
    /// Waits until <paramref name="needMb"/> fits in <paramref name="bucket"/>'s budget
    /// (evicting idle sessions in that bucket as needed), then takes the reservation.
    /// Never holds a reservation while waiting unless <paramref name="alreadyReserved"/> is set.
    /// Host-backed providers share the host RAM budget; accelerator providers share their
    /// device's VRAM budget.
    /// </summary>
    /// <param name="alreadyReserved">
    /// When <see langword="true"/>, the caller already holds the <paramref name="needMb"/>
    /// reservation in <paramref name="bucket"/> (a session that already exists and is resident),
    /// so the wait only brings the bucket's total — including that reservation — within budget
    /// and does not take a new one on success.
    /// </param>
    private async Task WaitForAdmissionBudgetAsync(
        long needMb,
        AdmissionBucket bucket,
        CancellationToken cancellationToken,
        bool alreadyReserved = false)
    {
        BenchmarkPhaseCapture.ObserveMaximum("admissionWaiters", Interlocked.Increment(ref admissionWaiters));
        try
        {
            int observedStallPasses = 0;
            bool hadLiveEntries = false;
            while (true)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();

                // Re-observe every iteration: this loop evicts idle work and then waits, so the
                // next pass can see the process's real usage fall as released sessions drain.
                // Host-RAM buckets are never charged with the observation (see
                // ObservedProcessGpuUsageMb), so they skip the probe instead of polling GPU
                // counters every 50 ms.
                ProcessGpuObservation? observation = bucket.IsHost ? null : ReadObservedProcessGpu();

                // A reservation already held for a resident session is part of the bucket's
                // current total; only a not-yet-reserved needMb is an additional charge.
                long chargeMb = alreadyReserved ? 0 : needMb;
                bool acquired = false;
                List<PoolEntry>? toDispose = null;
                List<KeyValuePair<Guid, ExternalReservationState>>? idleExternals = null;
                bool reservationBlocked;
                bool observedBlocked;
                await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);

                    // Eviction is bounded by what it can achieve: evicting a pooled entry lowers
                    // the reservation total but each eviction is re-observed before the next, so
                    // an observation-held admission converges instead of discarding the device's
                    // warm cache in one pass.
                    while (CurrentReservedMb(bucket) + chargeMb > BudgetFor(bucket))
                    {
                        PoolEntry? evicted = TryEvictLruIdle(onlyBucket: bucket);
                        if (evicted is null)
                        {
                            break;
                        }

                        toDispose ??= new List<PoolEntry>();
                        toDispose.Add(evicted);
                    }

                    reservationBlocked = CurrentReservedMb(bucket) + chargeMb > BudgetFor(bucket);
                    observedBlocked = !reservationBlocked
                        && AdmissionUsageMb(bucket, observation) + chargeMb > BudgetFor(bucket);

                    if (observedBlocked)
                    {
                        // Reservations now fit (the loop above may have evicted to get here):
                        // evict at most one idle entry per pass. Its real allocation sits inside
                        // the observation, so the next pass re-observes lower once that memory
                        // drains; evicting more per pass would throw away warm sessions faster
                        // than the observation can confirm they were the blockage.
                        PoolEntry? single = TryEvictLruIdle(onlyBucket: bucket);
                        if (single is not null)
                        {
                            toDispose ??= new List<PoolEntry>();
                            toDispose.Add(single);
                        }
                        else
                        {
                            // No pooled ONNX entry in this bucket is evictable; offer every
                            // idle external reservation a chance to release its resource,
                            // oldest first.
                            idleExternals = OrderedIdleExternals(bucket);
                        }
                    }
                    else if (reservationBlocked)
                    {
                        // No pooled ONNX entry in this bucket is evictable; offer every idle
                        // external reservation a chance to release its resource, oldest first.
                        idleExternals = OrderedIdleExternals(bucket);
                    }

                    if (!reservationBlocked && !observedBlocked)
                    {
                        if (!alreadyReserved)
                        {
                            AddPendingReservation(bucket, needMb);
                        }

                        acquired = true;
                    }
                }
                finally
                {
                    creationLock.Release();
                }

                if (toDispose is not null)
                {
                    foreach (PoolEntry entry in toDispose)
                    {
                        entry.Dispose();
                    }
                }

                if (acquired)
                {
                    return;
                }

                if (idleExternals is not null)
                {
                    // Invoke outside creationLock in LRU order: a refusal or throwing
                    // callback must not starve newer evictable candidates. Callbacks that
                    // declined stay registered — the resource may go idle later.
                    bool anyEvicted = false;
                    foreach (KeyValuePair<Guid, ExternalReservationState> candidate in idleExternals)
                    {
                        bool evictedExternal;
                        try
                        {
                            evictedExternal = candidate.Value.TryEvictIdle!();
                        }
                        catch
                        {
                            // A failing callback must not corrupt admission — treat as refusal.
                            evictedExternal = false;
                        }

                        if (evictedExternal)
                        {
                            // Defensive removal: a well-behaved callback already disposed
                            // the token.
                            externalReservations.TryRemove(candidate);
                            SignalBundleStateChanged();
                            anyEvicted = true;
                            // Turnover through eviction is progress for the stall bound: the
                            // freed memory needs a re-observe to drain, and a freshly-emptied
                            // bucket must not inherit the busy phase's accumulated passes.
                            observedStallPasses = 0;
                            break;
                        }
                    }

                    if (anyEvicted)
                    {
                        continue;
                    }
                }

                if (observedBlocked)
                {
                    // Any eviction this pass may still drain: the next pass re-observes, so only
                    // consecutive passes that free nothing count toward the fail-fast bound.
                    // Live-but-unevictable entries (held leases, pins, live externals) get the
                    // longer busy bound since their turnover can still unblock the wait; an
                    // empty bucket fails on the shorter bound.
                    bool progressed = toDispose is { Count: > 0 };
                    // Turnover that happens outside this loop — a live external released by its
                    // owner, or the last live entry going idle without being evicted here — is
                    // progress too: the busy bound existed to wait for it, so a live-to-empty
                    // transition must not trip the empty-bucket fail-fast on the very next pass
                    // before the freed memory has had a single re-observe to drain.
                    bool hasLiveEntries = BucketHasLiveEntries(bucket);
                    if (!hasLiveEntries && hadLiveEntries)
                    {
                        progressed = true;
                    }

                    hadLiveEntries = hasLiveEntries;
                    if (progressed)
                    {
                        observedStallPasses = 0;
                    }
                    else
                    {
                        observedStallPasses++;
                    }

                    int stallBound = hasLiveEntries
                        ? ObservedBlockBusyFailFastPasses
                        : ObservedBlockFailFastPasses;
                    if (observedStallPasses >= stallBound)
                    {
                        long attributedMb = ObservedProcessGpuUsageMb(bucket, observation);
                        throw new InvalidOperationException(
                            $"Accelerator admission for '{DescribeBucket(bucket)}' needs ~{needMb} MB, but ~{attributedMb} MB "
                            + "of dedicated GPU memory is attributed to this bucket against its admission budget of "
                            + $"{BudgetFor(bucket)} MB, with no evictable sessions or external reservations left to "
                            + "free. Free GPU memory, raise the budget "
                            + $"({SharedPoolOptions.BudgetMbVariable}), or opt out of process-GPU admission "
                            + $"({SharedPoolOptions.ProcessGpuAdmissionVariable}=0).");
                    }
                }
                else
                {
                    observedStallPasses = 0;
                }

                if (reservationBlocked || observedBlocked)
                {
                    // Doesn't fit yet — poll instead of blocking a thread-pool thread on
                    // Monitor.Wait. Bounded delay; loops back to retake creationLock and
                    // recheck (an eviction, a lease release, or the process's own GPU usage
                    // draining may have freed headroom).
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref admissionWaiters);
        }
    }

    /// <summary>
    /// Evicts every idle pooled session. Sessions currently leased out remain until released.
    /// </summary>
    /// <returns>The number of idle entries that were evicted and disposed.</returns>
    public async Task<int> EvictAllIdleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        List<PoolEntry> toDispose = new();
        int count = 0;

        await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            foreach (SessionPoolKey key in entries.Keys.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!entries.TryGetValue(key, out PoolEntry? entry))
                {
                    continue;
                }

                if (!TryAcquireGate(entry))
                {
                    continue;
                }

                if (entries.TryRemove(new KeyValuePair<SessionPoolKey, PoolEntry>(key, entry)))
                {
                    pooledCount--;
                    SignalBundleStateChanged();
                    entry.MarkEvicted();
                    toDispose.Add(entry);
                    count++;
                }
                else
                {
                    entry.Gate.Release();
                }
            }
        }
        finally
        {
            creationLock.Release();
        }

        foreach (PoolEntry entry in toDispose)
        {
            entry.Dispose();
        }

        return count;
    }

    /// <summary>
    /// Evicts all idle pool entries whose <see cref="SessionPoolKey.EngineFamily"/> matches
    /// <paramref name="engineFamily"/> and, when specified, whose
    /// <see cref="SessionPoolKey.ModelId"/> matches <paramref name="modelId"/>.
    /// In-use sessions (leased out) are not evicted.
    /// </summary>
    /// <returns>The number of entries that were evicted and disposed.</returns>
    public async Task<int> EvictModelAsync(
        string engineFamily,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        ObjectDisposedException.ThrowIf(disposed, this);

        List<PoolEntry> toDispose = new();
        int count = 0;

        await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            foreach (SessionPoolKey key in entries.Keys.ToArray())
            {
                bool familyMatch = string.Equals(key.EngineFamily, engineFamily, StringComparison.OrdinalIgnoreCase);
                bool modelMatch = modelId is null || string.Equals(key.ModelId, modelId, StringComparison.OrdinalIgnoreCase);

                if (familyMatch && modelMatch && entries.TryGetValue(key, out PoolEntry? entry))
                {
                    if (!TryAcquireGate(entry))
                    {
                        continue;
                    }

                    if (entries.TryRemove(new KeyValuePair<SessionPoolKey, PoolEntry>(key, entry)))
                    {
                        pooledCount--;
                        SignalBundleStateChanged();
                        entry.MarkEvicted();
                        toDispose.Add(entry); // dispose outside the lock below
                        count++;
                    }
                    else
                    {
                        entry.Gate.Release(); // entry removed by a concurrent eviction; release gate
                    }
                }
            }
        }
        finally
        {
            creationLock.Release();
        }

        // Dispose sessions outside the lock — session disposal can be expensive and
        // should not block other callers waiting on creationLock.
        foreach (PoolEntry entry in toDispose)
        {
            entry.Dispose();
        }

        return count;
    }

    /// <summary>
    /// Evicts idle pool entries until the estimated total VRAM footprint of pooled sessions
    /// is at or below <paramref name="targetVramMb"/>. Sessions currently leased out are not
    /// evicted. Returns the number of sessions evicted.
    /// </summary>
    /// <remarks>
    /// Eviction order: least-recently-released idle sessions first (same LRU order as the
    /// count-based eviction in <see cref="GetLeaseAsync"/>).
    /// </remarks>
    public async Task<int> TrimToVramBudgetAsync(long targetVramMb, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(targetVramMb);

        List<PoolEntry> toDispose = new();
        int count = 0;

        await creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            // Compute current total estimated VRAM of idle sessions
            long currentVram = entries.Sum(kvp => kvp.Value.IsIdle ? kvp.Key.EstimatedVramMb : 0);
            if (currentVram <= targetVramMb)
                return 0;

            // Sort idle entries by LRU (oldest first)
            var idleEntries = entries
                .Where(kvp => kvp.Value.IsIdle)
                .OrderBy(kvp => kvp.Value.LastReleasedTicks)
                .ToList();

            foreach (var (key, entry) in idleEntries)
            {
                if (currentVram <= targetVramMb)
                    break;

                cancellationToken.ThrowIfCancellationRequested();

                if (!TryAcquireGate(entry))
                    continue;

                if (entries.TryRemove(new KeyValuePair<SessionPoolKey, PoolEntry>(key, entry)))
                {
                    pooledCount--;
                    SignalBundleStateChanged();
                    entry.MarkEvicted();
                    currentVram -= key.EstimatedVramMb;
                    toDispose.Add(entry);
                    count++;
                }
                else
                {
                    entry.Gate.Release();
                }
            }
        }
        finally
        {
            creationLock.Release();
        }

        foreach (PoolEntry entry in toDispose)
            entry.Dispose();

        return count;
    }

    /// <summary>
    /// Disposes all idle pooled sessions.  Sessions currently leased out are left for their
    /// lease holders to release naturally.
    /// </summary>
    public void Dispose()
    {
        // Single-winner guard: only the first caller performs teardown.
        // Using Interlocked ensures concurrent Dispose() calls are safe and idempotent.
        if (Interlocked.Exchange(ref disposeOnce, 1) != 0)
        {
            return;
        }

        // Signal all code paths that check `disposed` so they throw ObjectDisposedException
        // instead of creating new sessions.  Must be set before acquiring the lock so that
        // slow-path waiters that win the lock after we release it observe the flag.
        disposed = true;

        List<PoolEntry> toDispose = new();

        if (creationLock.Wait(TimeSpan.FromSeconds(10)))
        {
            try
            {
                foreach (SessionPoolKey key in entries.Keys.ToArray())
                {
                    if (entries.TryRemove(key, out PoolEntry? entry))
                    {
                        pooledCount--;
                        SignalBundleStateChanged();
                        entry.MarkEvicted(); // defence-in-depth alongside `disposed` flag
                        // Atomically try to acquire the gate. If we win, schedule the entry for
                        // disposal outside the lock. If the entry is still leased, the BuildLease
                        // closure will observe disposed=true (and IsEvicted=true) on release and
                        // call entry.Dispose() idempotently.
                        if (TryAcquireGate(entry))
                        {
                            toDispose.Add(entry);
                        }
                    }
                }
            }
            finally
            {
                // Release the semaphore — do NOT dispose it.  Concurrent GetLeaseAsync slow-path
                // waiters may already be blocked on creationLock.WaitAsync(); disposing the
                // semaphore here would race with those waiters and surface as ObjectDisposedException
                // instead of the clean ObjectDisposedException.ThrowIf(disposed, this) path.
                // The semaphore is a small object and will be collected by the GC after the pool
                // itself is no longer referenced.
                pooledCount = 0;
                creationLock.Release();
            }
        }

        // Dispose sessions outside the lock — session disposal can be expensive and
        // should not block concurrent callers waiting on creationLock.
        foreach (PoolEntry entry in toDispose)
        {
            entry.Dispose();
        }

        // External reservations are dropped from accounting; each registered idle callback
        // is then attempted so a released external resource (e.g. a cached GenAI model) is
        // freed with the pool. A resource still leased may refuse eviction (false); its
        // token stays idempotent even though pool accounting is already cleared.
        foreach (KeyValuePair<Guid, ExternalReservationState> pair in externalReservations.ToArray())
        {
            if (!externalReservations.TryRemove(pair))
            {
                continue;
            }

            try
            {
                pair.Value.TryEvictIdle?.Invoke();
            }
            catch (ObjectDisposedException)
            {
                // The external owner may be disposing concurrently.
            }
            catch (InvalidOperationException)
            {
                // Teardown must tolerate an external owner that cannot evict now.
            }
            catch (Exception)
            {
                // Teardown must not throw or abandon the remaining reservations if a
                // native external resource fails while disposing.
            }
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private SessionLease BuildLease(PoolEntry entry)
    {
        return new SessionLease(entry.Session, () => ReleaseLeaseEntry(entry));
    }

    private void ReleaseLeaseEntry(PoolEntry entry)
    {
        if (entry.Ephemeral)
        {
            // Ephemeral entries are never stored in the pool; dispose immediately.
            // Admission mode never creates ephemeral sessions.
            entry.Dispose();
            return;
        }

        // Release the gate BEFORE checking eviction / disposal flags.  This closes the
        // handshake race: an eviction path that called MarkEvicted() while we held the gate
        // and then observed Gate.Wait(0)==false backed off, expecting us to clean up after
        // releasing.  After Release() the eviction path can atomically re-acquire the gate
        // and dispose, or — if it has already backed off — we detect the eviction below and
        // perform disposal ourselves.
        entry.Release();
        SignalBundleStateChanged();

        if (entry.IsEvicted || disposed)
        {
            // Re-check after releasing the gate: if an eviction path marked this entry
            // while the gate was held and left cleanup to us, attempt to re-acquire the
            // gate atomically.  If another caller (fast-path GetLeaseAsync or an eviction
            // path that beat us) already holds the gate, cleanup is theirs.
            try
            {
                if (entry.Gate.Wait(0))
                {
                    entry.Dispose();
                }
            }
            catch (ObjectDisposedException)
            {
                // Gate already disposed by a concurrent eviction / pool-dispose path.
            }
        }
    }

    /// <summary>
    /// Evicts the least-recently-released idle entry.
    /// Must be called while <see cref="creationLock"/> is held.
    /// </summary>
    /// <returns>
    /// The evicted <see cref="PoolEntry"/> (gate already acquired, removed from the pool, marked evicted)
    /// when an idle entry was found and successfully evicted; <see langword="null"/> when all entries are
    /// currently leased.  The caller is responsible for disposing the returned entry <em>outside</em>
    /// <see cref="creationLock"/> to avoid blocking unrelated pool operations during potentially expensive
    /// <see cref="InferenceSession"/> teardown.
    /// </returns>
    /// <summary>
    /// Evicts the least-recently-released idle entry, optionally restricted to one admission
    /// bucket (so a host RAM shortfall is not “fixed” by dropping GPU sessions, and a
    /// shortfall on accelerator device 0 is not “fixed” by dropping device 1 sessions).
    /// Must be called while <see cref="creationLock"/> is held.
    /// </summary>
    private PoolEntry? TryEvictLruIdle(AdmissionBucket? onlyBucket = null)
    {
        // Prefer evicting entries that have been idle for a while. Sessions released within
        // the recent window are treated as the active pipeline working set (e.g. the next
        // stage's graphs) and are only chosen when nothing older is idle.
        long now = Environment.TickCount64;
        long recentCutoff = now - RecentReleaseWindowMs;

        (SessionPoolKey? candidateKey, PoolEntry? candidateEntry) = FindOldestIdle(onlyBucket, recentCutoff);
        if (candidateEntry is null)
        {
            (candidateKey, candidateEntry) = FindOldestIdle(onlyBucket, recentCutoff: null);
        }

        if (candidateEntry is null)
        {
            return null;
        }

        if (!entries.TryGetValue(candidateKey!, out PoolEntry? evicted) || !TryAcquireGate(evicted))
        {
            return null;
        }

        bool removed = false;
        try
        {
            removed = entries.TryRemove(new KeyValuePair<SessionPoolKey, PoolEntry>(candidateKey!, evicted));
            if (removed)
            {
                pooledCount--;
                SignalBundleStateChanged();
                evicted.MarkEvicted();
                return evicted; // Caller must dispose outside creationLock.
            }
        }
        finally
        {
            if (!removed)
            {
                evicted.ReleaseGateWithoutTouchingLru();
            }
        }

        return null;
    }

    private (SessionPoolKey? Key, PoolEntry? Entry) FindOldestIdle(AdmissionBucket? onlyBucket, long? recentCutoff)
    {
        SessionPoolKey? candidateKey = null;
        PoolEntry? candidateEntry = null;
        long candidateLastReleasedTicks = long.MaxValue;
        foreach (KeyValuePair<SessionPoolKey, PoolEntry> pair in entries)
        {
            if (onlyBucket is not null && pair.Value.Bucket != onlyBucket.Value)
            {
                continue;
            }

            PoolEntry entry = pair.Value;
            if (!entry.IsIdle || entry.PinCount > 0)
            {
                continue;
            }

            long lastReleasedTicks = entry.LastReleasedTicks;
            if (recentCutoff is not null && lastReleasedTicks >= recentCutoff.Value)
            {
                continue;
            }

            if (candidateEntry is null || lastReleasedTicks < candidateLastReleasedTicks)
            {
                candidateKey = pair.Key;
                candidateEntry = entry;
                candidateLastReleasedTicks = lastReleasedTicks;
            }
        }

        return (candidateKey, candidateEntry);
    }

    /// <summary>
    /// Snapshot of live external reservations in <paramref name="bucket"/> that registered an
    /// idle-eviction callback, ordered least-recently-released first (Guid tie-break for
    /// determinism). Reservations still under construction have no callback and are never
    /// candidates — pending accounting already covers them.
    /// </summary>
    private List<KeyValuePair<Guid, ExternalReservationState>> OrderedIdleExternals(AdmissionBucket bucket)
    {
        List<KeyValuePair<Guid, ExternalReservationState>> candidates = externalReservations
            .Where(pair => pair.Value.Bucket == bucket && pair.Value.TryEvictIdle is not null)
            .ToList();

        candidates.Sort(static (x, y) =>
        {
            int c = x.Value.LastReleasedTicks.CompareTo(y.Value.LastReleasedTicks);
            return c != 0 ? c : x.Key.CompareTo(y.Key);
        });
        return candidates;
    }

    private static bool TryAcquireGate(PoolEntry entry)
    {
        try
        {
            return entry.Gate.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
