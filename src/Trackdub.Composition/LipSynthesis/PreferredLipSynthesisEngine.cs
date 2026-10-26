using Trackdub.Contracts.Pipeline;
using Trackdub.Media.LipSynthesis.NvidiaMaxine;

namespace Trackdub.Composition.LipSynthesis;

/// <summary>
/// Picks the lip-synthesis engine for each call: the one a model alias names, otherwise the first
/// available engine in preference order. Availability is re-checked on every call, so installing
/// or removing files is picked up; the engines' configuration itself is read once at startup.
/// </summary>
internal sealed class PreferredLipSynthesisEngine : ILipSynthesisEngine, IStageRuntimeExecutionReporter
{
    private readonly IReadOnlyList<ILipSynthesisEngine> enginesInPreferenceOrder;
    private ILipSynthesisEngine? lastUsed;

    public PreferredLipSynthesisEngine(IReadOnlyList<ILipSynthesisEngine> enginesInPreferenceOrder)
    {
        ArgumentNullException.ThrowIfNull(enginesInPreferenceOrder);
        if (enginesInPreferenceOrder.Count == 0)
        {
            throw new ArgumentException("At least one lip-synthesis engine is required.", nameof(enginesInPreferenceOrder));
        }

        this.enginesInPreferenceOrder = enginesInPreferenceOrder;
    }

    public bool IsAvailable => Default.IsAvailable;

    public bool IsExperimental => Default.IsExperimental;

    public string ProviderId => Default.ProviderId;

    public string ModelId => Default.ModelId;

    public StageRuntimeExecutionSummary? LastExecutionSummary =>
        (lastUsed as IStageRuntimeExecutionReporter)?.LastExecutionSummary;

    private ILipSynthesisEngine Default =>
        enginesInPreferenceOrder.FirstOrDefault(static engine => engine.IsAvailable) ?? enginesInPreferenceOrder[0];

    public Task<LipSynthesisResult> SynthesizeTurnAsync(
        LipSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ILipSynthesisEngine engine = Select(request.PreferredModelAlias);
        lastUsed = engine;
        return engine.SynthesizeTurnAsync(request, cancellationToken);
    }

    internal ILipSynthesisEngine Select(string? preferredModelAlias)
    {
        if (!string.IsNullOrWhiteSpace(preferredModelAlias))
        {
            bool wantsMaxine = preferredModelAlias.StartsWith(
                NvidiaMaxineLipSynthesisEngine.ModelAliasPrefix, StringComparison.OrdinalIgnoreCase);
            ILipSynthesisEngine? named = enginesInPreferenceOrder.FirstOrDefault(engine =>
                string.Equals(engine.ProviderId, NvidiaMaxineLipSynthesisEngine.ProviderIdValue, StringComparison.Ordinal) == wantsMaxine);
            if (named is not null)
            {
                return named;
            }
        }

        return Default;
    }
}
