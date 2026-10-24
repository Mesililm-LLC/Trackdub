using System.Collections.Concurrent;
using System.Threading.Channels;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Composition.Pipeline;

/// <summary>
/// Warms pipeline engines after a model install so the first real run does not pay TensorRT engine
/// construction. Work is serialized (engine builds are GPU-heavy and must not stack), coalesced per
/// model id, and runs on a background task that never throws into the caller.
/// </summary>
/// <remarks>
/// The trigger is an install, not a launch. A model downloaded mid-session had no EP-context
/// artifact, so its next run built engines from source and then rebuilt them on every launch after
/// that. Warming immediately after install produces the artifact while the user is still setting up.
/// Correctness never depends on this: a skipped or failed warm only means the cost moves back to
/// first use.
/// </remarks>
public sealed class EngineAutoWarmCoordinator : IEngineAutoWarmQueue, IDisposable
{
    /// <summary>Set to 0 or false to turn post-install engine warming off for the process.</summary>
    public const string DisableEnvironmentVariable = "TRACKDUB_ENGINE_AUTOWARM";

    private readonly IResidentWarmupService warmupService;
    private readonly BundledModelManifestRegistry manifestRegistry;
    private readonly IApplicationLogger? logger;
    private readonly Func<string, string?> readEnvironment;
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private int disposed;

    public EngineAutoWarmCoordinator(
        IResidentWarmupService warmupService,
        BundledModelManifestRegistry manifestRegistry,
        IApplicationLogger? logger = null,
        Func<string, string?>? readEnvironment = null)
    {
        this.warmupService = warmupService ?? throw new ArgumentNullException(nameof(warmupService));
        this.manifestRegistry = manifestRegistry ?? throw new ArgumentNullException(nameof(manifestRegistry));
        this.logger = logger;
        this.readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
        worker = Task.Run(ProcessQueueAsync);
    }

    public void RequestWarmAfterInstall(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsDisabled(readEnvironment(DisableEnvironmentVariable)) || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        // Coalesce: a model that is already queued or warming needs no second pass.
        if (!pending.TryAdd(modelId, 0))
        {
            return;
        }

        if (!queue.Writer.TryWrite(modelId))
        {
            pending.TryRemove(modelId, out _);
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        queue.Writer.TryComplete();
        await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stage whose planner task matches the installed model's manifest task. Warming exactly that
    /// stage keeps the work proportional to what was installed; a model no stage can select
    /// returns an empty list, so installing an unused model warms nothing.
    /// </summary>
    internal IReadOnlyList<RuntimeStage> ResolveStagesForModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        BundledModelManifestEntry? entry = manifestRegistry.Entries.FirstOrDefault(
            candidate => candidate.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (entry is null || string.IsNullOrWhiteSpace(entry.Task))
        {
            return [];
        }

        string task = entry.Task.Trim();
        return ResidentWarmupRequest.DefaultStages
            .Where(stage => string.Equals(ManifestTaskFor(stage), task, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Mirrors the stage-to-manifest-task mapping in StageRuntimeRequirementsCatalog (internal to
    /// Trackdub.Inference), the same way ControlledDubbingBenchmarkRunner does.
    /// </summary>
    internal static string? ManifestTaskFor(RuntimeStage stage) => stage switch
    {
        RuntimeStage.Vad => "vad",
        RuntimeStage.Asr => "asr",
        RuntimeStage.Translation => "translation",
        RuntimeStage.Tts => "tts",
        RuntimeStage.Diarization => "diarization",
        RuntimeStage.Separation => "separation",
        RuntimeStage.SpeechEnhancement => "speech-enhancement",
        RuntimeStage.LipSync => "forced-alignment",
        RuntimeStage.TextRefinement => "text-refinement",
        RuntimeStage.OverlapRescue => "overlap-rescue",
        RuntimeStage.LipSynthesis => "lip-synthesis",
        _ => null,
    };

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (string modelId in queue.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                // The pending entry is held for the whole warm, not released when the worker
                // dequeues: an install notification arriving while this model is warming must
                // coalesce into the pass already running rather than queue a duplicate.
                try
                {
                    await WarmAsync(modelId).ConfigureAwait(false);
                }
                finally
                {
                    pending.TryRemove(modelId, out _);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Shutdown: stop draining the queue.
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Post-install engine warm queue stopped unexpectedly.", ex);
        }
    }

    private async Task WarmAsync(string modelId)
    {
        try
        {
            IReadOnlyList<RuntimeStage> stages = ResolveStagesForModel(modelId);
            if (stages.Count == 0)
            {
                return;
            }

            logger?.LogInformation(
                $"Warming engines for '{modelId}' after install ({string.Join(", ", stages)}); "
                + "TensorRT engine artifacts are built now instead of on the first run.");

            ResidentWarmupReport report = await warmupService.WarmAsync(
                    new ResidentWarmupRequest(
                        stages,
                        new RuntimeModelSelections(AsrModelOverride.Auto, IsDevBuild: false, new Dictionary<string, ExecutionProviderKind>())),
                    progress: null,
                    lifetime.Token)
                .ConfigureAwait(false);

            logger?.LogInformation(
                report.Skipped
                    ? $"Post-install engine warm skipped for '{modelId}': {report.SkipReason}"
                    : $"Post-install engine warm finished for '{modelId}': "
                      + string.Join(", ", report.Stages.Select(static stage =>
                          $"{stage.Stage}={(stage.Succeeded ? "warm" : stage.Attempted ? "failed" : "skipped")}")));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // Optimization only: a failed warm must never surface as an install failure.
            logger?.LogWarning($"Post-install engine warm failed for '{modelId}'; it will load on first use.", ex);
        }
    }

    private static bool IsDisabled(string? value) =>
        value is not null
        && (value.Trim() == "0" || value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        queue.Writer.TryComplete();
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        try
        {
            // A native engine build cannot be interrupted; never block shutdown on one.
            worker.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Cancellation surfaces here; shutdown must not fail on it.
        }

        lifetime.Dispose();
    }
}
