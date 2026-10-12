using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// The planner's global provider probe order, best first, for callers that pick an EP for a
/// model outside any runtime stage (resident serving).
/// </summary>
public static class ExecutionProviderProbeOrder
{
    public static IReadOnlyList<ExecutionProviderKind> Global =>
        Milestone5PlanningPolicy.SupportedProvidersThisMilestone;

    /// <summary>
    /// Discovered providers that this process can use for any model, in <see cref="Global"/>
    /// order. Excludes availability scoped to specific engine families, which is reachable only
    /// through another host (the inference worker) rather than an in-process session.
    /// </summary>
    public static IReadOnlyList<ExecutionProviderKind> InProcessAvailable(
        IEnumerable<ExecutionProviderAvailability> availability)
    {
        HashSet<ExecutionProviderKind> usable = availability
            .Where(static item => item.IsAvailable && item.EngineFamilies is null)
            .Select(static item => item.Provider)
            .ToHashSet();
        return Global.Where(usable.Contains).ToList();
    }
}
