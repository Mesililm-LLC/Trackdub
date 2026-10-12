using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Trackdub.Application.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Pipeline;

/// <summary>
/// Startup counterpart to the in-run next-stage warmup: registers the discovered execution
/// providers, then runs <see cref="IStageWarmupCoordinator"/> for each requested stage in
/// order, leaving each stage's planner-selected session warm in the pool.
/// </summary>
public sealed class ResidentWarmupService : IResidentWarmupService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IExecutionProviderDiscovery discovery;
    private readonly IHardwareProfileProvider hardwareProfileProvider;
    private readonly Func<ExecutionProviderKind, CancellationToken, Task<ResidentProviderBootstrapOutcome>> bootstrapProvider;
    private readonly Func<string, string?> readEnvironment;

    public ResidentWarmupService(
        IServiceScopeFactory scopeFactory,
        IExecutionProviderDiscovery discovery,
        IHardwareProfileProvider hardwareProfileProvider)
        : this(scopeFactory, discovery, hardwareProfileProvider, BootstrapAsync, Environment.GetEnvironmentVariable)
    {
    }

    internal ResidentWarmupService(
        IServiceScopeFactory scopeFactory,
        IExecutionProviderDiscovery discovery,
        IHardwareProfileProvider hardwareProfileProvider,
        Func<ExecutionProviderKind, CancellationToken, Task<ResidentProviderBootstrapOutcome>> bootstrapProvider,
        Func<string, string?> readEnvironment)
    {
        this.scopeFactory = scopeFactory;
        this.discovery = discovery;
        this.hardwareProfileProvider = hardwareProfileProvider;
        this.bootstrapProvider = bootstrapProvider;
        this.readEnvironment = readEnvironment;
    }

    public async Task<ResidentWarmupReport> WarmAsync(
        ResidentWarmupRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsDisabled(readEnvironment(IResidentWarmupService.DisableEnvironmentVariable)))
        {
            return new ResidentWarmupReport(
                Skipped: true,
                $"{IResidentWarmupService.DisableEnvironmentVariable} turned resident warmup off.",
                [],
                []);
        }

        IReadOnlyList<ResidentProviderBootstrapOutcome> providers = request.BootstrapProviders
            ? await BootstrapProvidersAsync(progress, cancellationToken).ConfigureAwait(false)
            : [];

        var stages = new List<ResidentStageWarmupOutcome>(request.Stages.Count);
        using IServiceScope scope = scopeFactory.CreateScope();
        IStageWarmupCoordinator coordinator = scope.ServiceProvider.GetRequiredService<IStageWarmupCoordinator>();
        foreach (RuntimeStage stage in request.Stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Warming {stage}...");
            var stopwatch = Stopwatch.StartNew();
            StageWarmupResult result;
            try
            {
                result = await coordinator.WarmAsync(
                    new StageWarmupRequest(
                        stage,
                        request.Selections,
                        request.SourceLanguageCode,
                        request.TargetLanguageCode,
                        request.RequiresVoiceClone),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                result = new StageWarmupResult(Attempted: true, Succeeded: false, ex.Message);
            }
            catch (ArgumentException ex)
            {
                result = new StageWarmupResult(Attempted: true, Succeeded: false, ex.Message);
            }

            stopwatch.Stop();
            stages.Add(new ResidentStageWarmupOutcome(
                stage, result.Attempted, result.Succeeded, stopwatch.Elapsed, result.Detail));
            progress?.Report(result.Succeeded
                ? $"{stage} warm in {stopwatch.Elapsed.TotalSeconds:F1} s."
                : $"{stage} not warmed: {result.Detail ?? "no detail"}");
        }

        return new ResidentWarmupReport(Skipped: false, SkipReason: null, providers, stages);
    }

    private async Task<IReadOnlyList<ResidentProviderBootstrapOutcome>> BootstrapProvidersAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ExecutionProviderAvailability> availability;
        try
        {
            HardwareProfile profile = await hardwareProfileProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            availability = await discovery.DiscoverAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report($"Provider discovery failed: {ex.Message}");
            return [];
        }

        // CPU needs no registration.
        var outcomes = new List<ResidentProviderBootstrapOutcome>();
        foreach (ExecutionProviderKind provider in ExecutionProviderProbeOrder.InProcessAvailable(availability)
                     .Where(static provider => provider is not ExecutionProviderKind.Cpu))
        {
            ResidentProviderBootstrapOutcome outcome;
            try
            {
                outcome = await bootstrapProvider(provider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = new ResidentProviderBootstrapOutcome(provider, Ready: false, ex.Message);
            }

            outcomes.Add(outcome);
            progress?.Report($"{ExecutionProviderTokens.ToCanonicalTag(provider)}: {(outcome.Ready ? "registered" : outcome.Detail)}");
        }

        return outcomes;
    }

    private static async Task<ResidentProviderBootstrapOutcome> BootstrapAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        var result = await OnnxExecutionSessionFactory.BootstrapForSmokeAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        return new ResidentProviderBootstrapOutcome(provider, result.IsRequestFulfilled, result.FailureReason ?? result.Detail);
    }

    private static bool IsDisabled(string? value) =>
        value is not null
        && (value.Trim() == "0" || value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));
}
