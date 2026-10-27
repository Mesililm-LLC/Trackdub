using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Tests;

/// <summary>
/// C3: multi-graph pooled keys must hash their graphs concurrently. The digest source is a
/// gated fake: each hash signals entry and then blocks until the test releases it, so overlap
/// is proven by synchronization, never by wall-clock timing.
/// </summary>
public sealed class PooledKeyParallelBuildTests
{
    [Fact]
    public async Task CreateManyAsync_HashesAllGraphsConcurrently()
    {
        var entered = new TaskCompletionSource[4];
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int enteredCount = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (int i = 0; i < entered.Length; i++)
        {
            entered[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Func<string, CancellationToken, Task<string?>> gatedHasher = async (path, ct) =>
        {
            int slot = Interlocked.Increment(ref enteredCount) - 1;
            entered[slot].TrySetResult();
            if (slot == entered.Length - 1)
            {
                allEntered.TrySetResult();
            }

            await release.Task.WaitAsync(ct);
            return "hash-" + Path.GetFileName(path);
        };

        try
        {
            var requests = new[]
            {
                new SessionPoolKey.PooledKeyRequest("fam", null, null, ExecutionProviderKind.Cpu, "a.onnx", null, "encoder", "fp"),
                new SessionPoolKey.PooledKeyRequest("fam", null, null, ExecutionProviderKind.Cpu, "b.onnx", null, "decoder", "fp"),
                new SessionPoolKey.PooledKeyRequest("fam", null, null, ExecutionProviderKind.Cpu, "c.onnx", null, "decoder-init", "fp"),
                new SessionPoolKey.PooledKeyRequest("fam", null, null, ExecutionProviderKind.Cpu, "d.onnx", null, "decoder-step", "fp"),
            };

            Task<SessionPoolKey[]> build = SessionPoolKey.CreateManyAsync(requests, CancellationToken.None, gatedHasher);

            // Serial hashing would enter only the first hash and then block on the gate.
            await allEntered.Task.AwaitWithHangGuard("all four graph hashes never started concurrently");
            Assert.Equal(4, enteredCount);
            Assert.False(build.IsCompleted);

            release.TrySetResult();
            SessionPoolKey[] keys = await build.AwaitWithHangGuard("parallel key build never completed");
            Assert.Equal(4, keys.Length);
            Assert.Equal(
                ["encoder", "decoder", "decoder-init", "decoder-step"],
                keys.Select(k => k.GraphRole).ToArray());
            Assert.All(keys, k => Assert.StartsWith("hash-", k.ModelContentHash, StringComparison.Ordinal));
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
