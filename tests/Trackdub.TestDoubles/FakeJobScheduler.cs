using Trackdub.Cloud.Jobs;

namespace Trackdub.TestDoubles;

public sealed class FakeJobScheduler : IJobScheduler
{
    public Dictionary<Guid, List<SchedulerExecution>> Executions { get; } = [];
    public int Submissions { get; private set; }
    public bool LoseSubmitResponse { get; set; }
    public Task<SchedulerExecution> SubmitAsync(DispatchRequest request, CancellationToken token)
    {
        Submissions++;
        var execution = new SchedulerExecution(Guid.NewGuid().ToString(), false, null);
        if (!Executions.TryGetValue(request.DispatchId, out var list)) Executions[request.DispatchId] = list = [];
        list.Add(execution);
        if (LoseSubmitResponse) throw new IOException("Simulated lost submit response.");
        return Task.FromResult(execution);
    }
    public Task<IReadOnlyList<SchedulerExecution>> DescribeAsync(Guid dispatchId, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<SchedulerExecution>>(Executions.GetValueOrDefault(dispatchId)?.ToArray() ?? []);
    public Task StopAsync(string executionId, string reason, CancellationToken token)
    {
        foreach (var list in Executions.Values)
            for (int i = 0; i < list.Count; i++) if (list[i].Id == executionId) list[i] = list[i] with { Stopped = true, ExitCode = 137 };
        return Task.CompletedTask;
    }
}
