using Trackdub.Inference.Onnx.EpContext;

namespace Trackdub.Inference.Onnx.Tests;

/// <summary>
/// A skipped EP-context capture means TensorRT engines are rebuilt from source on every launch,
/// so the reason must be reportable. The critical property is that a space-driven skip stays
/// transient: recovering disk must re-enable capture without an app restart, or a temporarily
/// full disk would silently degrade every future launch.
/// </summary>
public sealed class EpContextCaptureSkipTests
{
    private const long MinimumCapturedModelBytes = 256L * 1024 * 1024;

    [Fact]
    public void Small_graph_explains_itself_and_is_not_a_capture_candidate()
    {
        string directory = CreateTempDirectory();
        string modelPath = Path.Join(directory, "small.onnx");

        try
        {
            File.WriteAllBytes(modelPath, new byte[1024]);

            Assert.False(EpContextCapture.ShouldCapture(modelPath));
            Assert.True(EpContextCapture.TryGetSkipReason(modelPath, out string reason));
            Assert.Contains("small enough", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Existing_ep_context_artifact_is_never_recaptured()
    {
        string directory = CreateTempDirectory();
        // Artifacts are named "<model>.epc.onnx" (see EpContextArtifact.GetEpContextPath).
        string artifactPath = Path.Join(directory, "model.epc.onnx");

        try
        {
            File.WriteAllBytes(artifactPath, [1, 2, 3]);

            Assert.False(EpContextCapture.ShouldCapture(artifactPath));
            Assert.True(EpContextCapture.TryGetSkipReason(artifactPath, out string reason));
            Assert.Contains("EP-context artifact", reason, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Capture_candidate_with_headroom_reports_no_skip_reason()
    {
        string directory = CreateTempDirectory();
        string modelPath = Path.Join(directory, "large.onnx");

        try
        {
            // Sparse file: reports the full length without consuming the bytes.
            using (FileStream stream = File.Create(modelPath))
            {
                stream.SetLength(MinimumCapturedModelBytes + 1);
            }

            long available = new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;
            bool enoughRoom = available >= (MinimumCapturedModelBytes + 1) * 2;

            bool hasReason = EpContextCapture.TryGetSkipReason(modelPath, out string reason);

            // Assert against the real headroom so the test is meaningful on either kind of host.
            Assert.Equal(!enoughRoom, hasReason);
            if (enoughRoom)
            {
                Assert.Equal(string.Empty, reason);
            }
            else
            {
                Assert.Equal(EpContextCapture.InsufficientDiskSpaceReason, reason);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Space_skip_reason_names_the_consequence_operators_need_to_act_on()
    {
        // The message has to say what goes wrong, not just that capture was skipped.
        Assert.Contains("rebuild", EpContextCapture.InsufficientDiskSpaceReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disk space", EpContextCapture.InsufficientDiskSpaceReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Space_skip_is_re_evaluated_not_memoized()
    {
        // Repeated queries with unchanged free space must agree, and neither call may latch a
        // sticky "failed" entry: the check derives from live drive state, never from a cache.
        string directory = CreateTempDirectory();
        string modelPath = Path.Join(directory, "large.onnx");

        try
        {
            using (FileStream stream = File.Create(modelPath))
            {
                stream.SetLength(MinimumCapturedModelBytes + 1);
            }

            bool firstHasReason = EpContextCapture.TryGetSkipReason(modelPath, out string firstReason);
            bool secondHasReason = EpContextCapture.TryGetSkipReason(modelPath, out string secondReason);

            Assert.Equal(firstHasReason, secondHasReason);
            Assert.Equal(firstReason, secondReason);

            // The space outcome must never be recorded in the sticky snapshot, so recovering
            // disk re-enables capture without restarting the process.
            IReadOnlyDictionary<string, string> snapshot = EpContextCapture.SnapshotCaptureSkips();
            Assert.False(snapshot.ContainsKey(Path.GetFullPath(modelPath)));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Snapshot_reports_small_graph_skips_for_diagnostics()
    {
        string directory = CreateTempDirectory();
        string modelPath = Path.Join(directory, "tiny.onnx");

        try
        {
            File.WriteAllBytes(modelPath, new byte[64]);
            Assert.False(EpContextCapture.ShouldCapture(modelPath));

            IReadOnlyDictionary<string, string> snapshot = EpContextCapture.SnapshotCaptureSkips();
            Assert.True(snapshot.ContainsKey(Path.GetFullPath(modelPath)));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Join(Path.GetTempPath(), $"trackdub-epc-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }
}
