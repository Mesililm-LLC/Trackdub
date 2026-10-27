using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Composition.Runtime;

internal static class RequiredModelFileHashVerifier
{
    // Hashing is disk-bound; a small fixed fan-out avoids saturating storage with model files.
    internal const int MaxConcurrentHashes = 3;

    internal sealed record Candidate(string RelativePath, string FilePath, string ExpectedSha256);

    internal static async Task<IReadOnlyList<(Candidate Candidate, HashVerificationResult Result)>> VerifyAsync(
        IReadOnlyList<Candidate> candidates,
        IModelHashVerifier hashVerifier,
        CancellationToken cancellationToken)
    {
        var results = new HashVerificationResult?[candidates.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentHashes,
                CancellationToken = cancellationToken
            },
            async (index, ct) =>
            {
                Candidate candidate = candidates[index];
                results[index] = await hashVerifier
                    .VerifyAsync(candidate.ExpectedSha256, candidate.FilePath, ct)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

        return Enumerable.Range(0, candidates.Count)
            .Select(index => (candidates[index], results[index]!))
            .ToArray();
    }
}
