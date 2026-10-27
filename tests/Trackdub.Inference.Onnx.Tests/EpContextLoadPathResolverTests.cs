using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.EpContext;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Tests;

/// <summary>
/// The pre-warmup caveat from a780df91: until host startup / warmup loads the hardware
/// identity, the resolver holds the placeholder ("unknown", null). Lookups in that state
/// must be honest misses, stamps must never be written from the placeholder, and once the
/// identity is loaded a correctly stamped artifact must resolve — while a mismatched one
/// still must not.
/// </summary>
public sealed class EpContextLoadPathResolverTests
{
    private const string Driver = "32.0.16.1714";
    private static readonly IReadOnlyDictionary<string, string> NoOptions =
        new Dictionary<string, string>();

    [Fact]
    public void Lookup_before_identity_is_loaded_misses_and_writes_no_unknown_stamp()
    {
        string directory = CreateTempDirectory();
        string sourcePath = Path.Join(directory, "model.onnx");
        try
        {
            EpContextLoadPathResolver.ResetCurrentHardwareForTesting();
            WriteArtifact(sourcePath, "Blackwell", Driver);

            Assert.False(EpContextLoadPathResolver.HasIdentifiedHardware);
            // The artifact itself is valid for its real fingerprint...
            EpContextArtifact.Stamp stamp = EpContextArtifact.TryReadStamp(EpContextArtifact.GetStampPath(sourcePath))!;
            Assert.Equal(
                EpContextArtifact.GetEpContextPath(sourcePath),
                EpContextArtifact.TryResolveValidLoadPath(sourcePath, stamp.EnvironmentFingerprint, Identity(sourcePath)));
            // ...but the resolver must not resolve it against the placeholder fingerprint.
            Assert.Null(EpContextLoadPathResolver.TryResolveLoadPath(sourcePath, NoOptions));

            // Nor may a stamp be persisted from the placeholder identity.
            EpContextArtifact.Stamp unknownStamp = EpContextArtifact.CreateStamp(
                sourcePath, new FileInfo(sourcePath), sourceSha256: null,
                EpContextLoadPathResolver.CurrentHardware.GpuArchitecture,
                EpContextLoadPathResolver.CurrentHardware.DriverVersion);
            string otherSource = Path.Join(directory, "other.onnx");
            File.WriteAllBytes(otherSource, [1, 2, 3, 4]);
            Assert.Throws<InvalidOperationException>(() => EpContextArtifact.WriteStamp(otherSource, unknownStamp));
            Assert.False(File.Exists(EpContextArtifact.GetStampPath(otherSource)));
        }
        finally
        {
            EpContextLoadPathResolver.ResetCurrentHardwareForTesting();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Publish_with_unknown_stamp_refuses_and_preserves_the_existing_artifact()
    {
        string directory = CreateTempDirectory();
        string sourcePath = Path.Join(directory, "model.onnx");
        try
        {
            EpContextLoadPathResolver.ResetCurrentHardwareForTesting();
            WriteArtifact(sourcePath, "Blackwell", Driver);
            byte[] artifactBefore = File.ReadAllBytes(EpContextArtifact.GetEpContextPath(sourcePath));
            byte[] stampBefore = File.ReadAllBytes(EpContextArtifact.GetStampPath(sourcePath));

            string staging = Path.Join(directory, ".stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(
                Path.Join(staging, Path.GetFileName(EpContextArtifact.GetEpContextPath(sourcePath))), [9, 9, 9]);

            // This is the pre-warmup capture/compile path: CurrentHardware is still the
            // placeholder, so CreateStamp would stamp 'unknown'.
            Assert.Throws<InvalidOperationException>(() => EpContextArtifact.PublishStagedArtifact(
                staging, sourcePath,
                createStamp: files => EpContextArtifact.CreateStamp(
                    sourcePath, new FileInfo(sourcePath), sourceSha256: null,
                    EpContextLoadPathResolver.CurrentHardware.GpuArchitecture,
                    EpContextLoadPathResolver.CurrentHardware.DriverVersion, files)));

            Assert.Equal(artifactBefore, File.ReadAllBytes(EpContextArtifact.GetEpContextPath(sourcePath)));
            Assert.Equal(stampBefore, File.ReadAllBytes(EpContextArtifact.GetStampPath(sourcePath)));
        }
        finally
        {
            EpContextLoadPathResolver.ResetCurrentHardwareForTesting();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task After_identity_is_loaded_a_correctly_stamped_artifact_resolves()
    {
        string directory = CreateTempDirectory();
        string sourcePath = Path.Join(directory, "model.onnx");
        try
        {
            EpContextLoadPathResolver.ResetCurrentHardwareForTesting();
            WriteArtifact(sourcePath, "Blackwell", Driver);
            Assert.Null(EpContextLoadPathResolver.TryResolveLoadPath(sourcePath, NoOptions));

            var warmup = new EpContextWarmupService(
                new FakeStoragePaths(directory),
                new FakeHardwareProfileProvider(Profile(NvidiaGpuArchitectureBucket.Blackwell, Driver)));
            await warmup.InitializeHardwareIdentityAsync(TestContext.Current.CancellationToken);

            Assert.True(EpContextLoadPathResolver.HasIdentifiedHardware);
            // Resolution through the exact fingerprint + compile-options identity the
            // resolver passes on the session-creation path. (The resolver's final
            // device-compatibility gate additionally needs a live TRT-RTX device.)
            Assert.Equal(
                EpContextArtifact.GetEpContextPath(sourcePath),
                EpContextArtifact.TryResolveValidLoadPath(
                    sourcePath,
                    EpContextLoadPathResolver.CurrentEnvironmentFingerprint,
                    EpContextLoadPathResolver.BuildCompileOptionsIdentity(sourcePath, NoOptions)));
        }
        finally
        {
            EpContextLoadPathResolver.ResetCurrentHardwareForTesting();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Mismatched_arch_or_driver_artifact_is_still_rejected_after_identity_is_loaded()
    {
        string directory = CreateTempDirectory();
        string sourcePath = Path.Join(directory, "model.onnx");
        try
        {
            WriteArtifact(sourcePath, "Blackwell", Driver);

            // Same driver, different architecture.
            EpContextLoadPathResolver.SetCurrentHardware(Profile(NvidiaGpuArchitectureBucket.Ada, Driver));
            Assert.Null(EpContextArtifact.TryResolveValidLoadPath(
                sourcePath, EpContextLoadPathResolver.CurrentEnvironmentFingerprint, Identity(sourcePath)));

            // Same architecture, different driver.
            EpContextLoadPathResolver.SetCurrentHardware(Profile(NvidiaGpuArchitectureBucket.Blackwell, "560.35.03"));
            Assert.Null(EpContextArtifact.TryResolveValidLoadPath(
                sourcePath, EpContextLoadPathResolver.CurrentEnvironmentFingerprint, Identity(sourcePath)));
            Assert.Null(EpContextLoadPathResolver.TryResolveLoadPath(sourcePath, NoOptions));
        }
        finally
        {
            EpContextLoadPathResolver.ResetCurrentHardwareForTesting();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HardwareProfile Profile(NvidiaGpuArchitectureBucket architecture, string? driver) =>
        new("windows", "X64", HasGpu: true,
            NvidiaGpuArchitecture: architecture, GpuDriverVersion: driver);

    private static string Identity(string sourcePath) =>
        EpContextLoadPathResolver.BuildCompileOptionsIdentity(sourcePath, NoOptions);

    private static void WriteArtifact(string sourcePath, string architecture, string driver)
    {
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        File.WriteAllBytes(EpContextArtifact.GetEpContextPath(sourcePath), [5, 6, 7, 8]);
        EpContextArtifact.Stamp stamp = EpContextArtifact.CreateStamp(
            sourcePath, new FileInfo(sourcePath), sourceSha256: null,
            gpuArchitecture: architecture, driverVersion: driver,
            compileOptionsIdentity: Identity(sourcePath));
        EpContextArtifact.WriteStamp(sourcePath, stamp);
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Join(Path.GetTempPath(), $"trackdub-epc-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FakeHardwareProfileProvider(HardwareProfile profile) : IHardwareProfileProvider
    {
        public Task<HardwareProfile> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);
    }

    private sealed class FakeStoragePaths(string root) : IAppStoragePaths
    {
        public string RootDirectory => root;
        public string UserDataRoot => root;
        public string UserCacheRoot => root;
        public string? SharedAssetRoot => null;
        public bool IsPortable => false;
        public string ModelCacheDirectory => root;
        public string ModelCacheIndexPath => Path.Join(root, "index.json");
        public string LogFilePath => Path.Join(root, "log.txt");
        public string SettingsPath => Path.Join(root, "settings.json");
        public string LayoutPath => Path.Join(root, "layout.json");
        public string ToolCacheDirectory => root;
        public string FfmpegToolCacheDirectory => root;
        public string EngineCacheDirectory => root;
        public string ComponentCacheDirectory => root;
    }
}
