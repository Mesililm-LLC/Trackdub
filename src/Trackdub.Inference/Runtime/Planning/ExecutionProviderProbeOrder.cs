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
}
