using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Composition.Runtime;

namespace Trackdub.Composition.Tests;

public sealed class RequiredModelFileHashVerifierTests
{
    [Fact]
    public async Task VerifyAsync_overlaps_hashes_and_reports_first_invalid_manifest_entry()
    {
        var verifier = new OverlappingHashVerifier();
        RequiredModelFileHashVerifier.Candidate[] candidates =
        [
            new("valid.onnx", "valid.onnx", "expected-valid"),
            new("corrupt.onnx", "corrupt.onnx", "expected-corrupt"),
            new("later-corrupt.onnx", "later-corrupt.onnx", "expected-later")
        ];

        IReadOnlyList<(RequiredModelFileHashVerifier.Candidate Candidate, HashVerificationResult Result)> results =
            await RequiredModelFileHashVerifier.VerifyAsync(candidates, verifier, CancellationToken.None);

        Assert.True(verifier.ObservedOverlap);
        (RequiredModelFileHashVerifier.Candidate failed, HashVerificationResult failure) =
            results.First(item => !item.Result.IsValid);
        Assert.Equal("corrupt.onnx", failed.RelativePath);
        Assert.Equal("expected-corrupt", failure.ExpectedSha256);
    }

    private sealed class OverlappingHashVerifier : IModelHashVerifier
    {
        private int started;
        private readonly TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource laterCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedOverlap { get; private set; }

        public async Task<HashVerificationResult> VerifyAsync(
            string? expectedSha256,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref started) >= 2)
            {
                ObservedOverlap = true;
                allStarted.TrySetResult();
            }

            await allStarted.Task.WaitAsync(cancellationToken);
            if (expectedSha256 == "expected-corrupt")
            {
                await laterCompleted.Task.WaitAsync(cancellationToken);
            }

            bool valid = expectedSha256 == "expected-valid";
            if (expectedSha256 == "expected-later")
            {
                laterCompleted.TrySetResult();
            }

            return new HashVerificationResult(valid, true, expectedSha256, "actual", valid ? null : "corrupt");
        }

        public Task<HashVerificationResult> VerifyAsync(
            ModelManifest manifest,
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
