using System.Text.Json;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.TensorRtRtx;
using Trackdub.Infrastructure.Runtime.TrtRtxEp;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Keeps the hardcoded TRT RTX pin (constants) and the shipped manifest from drifting apart.
/// </summary>
public sealed class TrtRtxBundlePinTests
{
    [Fact]
    public void Repo_manifest_versions_match_bundled_constants_per_platform()
    {
        TrtRtxEpBundleManifest manifest = TrtRtxEpBundleManifestLoader.Load(ResolveRepoManifestPath());

        Assert.Equal(TensorRtRtxProviderConstants.BundledVersionWindows, manifest.ResolveVersion("win-x64"));
        Assert.Equal(TensorRtRtxProviderConstants.BundledVersionLinux, manifest.ResolveVersion("linux-x64"));
        Assert.Equal(TensorRtRtxProviderConstants.BundledCudaVariant, manifest.CudaVariant);
        foreach ((string rid, TrtRtxEpBundlePackage package) in manifest.Packages)
        {
            Assert.Contains($"/v{manifest.ResolveVersion(rid)}/", package.ArchiveUrl, StringComparison.Ordinal);
            Assert.Contains(manifest.CudaVariant, package.ArchiveUrl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Repo_manifest_runtime_version_matches_bundled_runtime_constant()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResolveRepoManifestPath()));

        // The manifest drives the Windows bundle; Linux follows LinML.Catalog's pinned wheel.
        Assert.Equal(
            TensorRtRtxProviderConstants.BundledTrtRtxRuntimeVersionWindows,
            document.RootElement.GetProperty("trtRtxRuntimeVersion").GetString());
    }

    [Fact]
    public void Windows_runtime_file_names_encode_the_bundled_runtime_major_minor()
    {
        string[] parts = TensorRtRtxProviderConstants.BundledTrtRtxRuntimeVersionWindows.Split('.');
        string suffix = $"_{parts[0]}_{parts[1]}.dll";

        Assert.EndsWith(suffix, TensorRtRtxProviderConstants.TensorRtRuntimeFileNameWindows, StringComparison.Ordinal);
        Assert.EndsWith(suffix, TensorRtRtxProviderConstants.TensorRtOnnxParserFileNameWindows, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_required_files_match_locator_required_files()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            // The TensorRT-RTX EP ABI plugin ships only for Windows and Linux;
            // TrtRtxEpRequiredFiles.RequiredFileNames throws PlatformNotSupportedException
            // elsewhere (macOS CI), so there is no installer/locator set to compare.
            return;
        }

        Assert.Equal(TensorRtRtxProviderConstants.RequiredPluginFileNames, TrtRtxEpRequiredFiles.RequiredFileNames);
    }

    [Fact]
    public void Fingerprint_version_changes_with_runtime_lineage_not_only_ep_abi_version()
    {
        string fingerprint = TensorRtRtxProviderConstants.BundledFingerprintVersion;

        Assert.StartsWith(TensorRtRtxProviderConstants.BundledVersion, fingerprint, StringComparison.Ordinal);
        Assert.Contains(TensorRtRtxProviderConstants.BundledTrtRtxRuntimeVersion, fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_install_directory_uses_platform_version_and_cuda_variant()
    {
        string root = Path.Join(Path.GetTempPath(), "trackdub-pin");
        string directory = TensorRtRtxProviderConstants.GetDefaultInstallDirectory(root, "win-x64");

        Assert.Equal(
            Path.Join(
                Path.GetFullPath(root),
                "Providers",
                "trt-rtx",
                TensorRtRtxProviderConstants.BundledVersion,
                TensorRtRtxProviderConstants.BundledCudaVariant,
                "win-x64"),
            directory);
    }

    [Fact]
    public void Resolve_skips_superseded_managed_bundle_persisted_in_settings_and_uses_current_bundle()
    {
        string root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "trackdub-pin", "Providers", "trt-rtx"));
        string stale = Path.Join(root, "0.3.0", "cu12", "win-x64");
        string current = Path.Join(root, TensorRtRtxProviderConstants.BundledVersion, TensorRtRtxProviderConstants.BundledCudaVariant, "win-x64");

        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            explicitPluginDirectory: stale,
            defaultInstallDirectory: current,
            getEnvironmentVariable: _ => null,
            directoryExists: _ => true,
            fileExists: path => path.StartsWith(current, StringComparison.OrdinalIgnoreCase));

        Assert.True(resolution.Succeeded);
        Assert.Equal(TensorRtRtxPluginDirectorySource.InstalledBundle, resolution.Source);
        Assert.Equal(current, resolution.DirectoryPath);
    }

    [Fact]
    public void Resolve_skips_superseded_managed_bundle_from_environment_variable()
    {
        string root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "trackdub-pin", "Providers", "trt-rtx"));
        string stale = Path.Join(root, "0.3.0", "cu12", "win-x64");
        string current = Path.Join(root, TensorRtRtxProviderConstants.BundledVersion, TensorRtRtxProviderConstants.BundledCudaVariant, "win-x64");

        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            explicitPluginDirectory: null,
            defaultInstallDirectory: current,
            getEnvironmentVariable: name =>
                name == TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable ? stale : null,
            directoryExists: _ => true,
            fileExists: path => path.StartsWith(current, StringComparison.OrdinalIgnoreCase));

        Assert.True(resolution.Succeeded);
        Assert.Equal(TensorRtRtxPluginDirectorySource.InstalledBundle, resolution.Source);
    }

    [Fact]
    public void Resolve_keeps_user_chosen_directory_authoritative_even_when_incomplete()
    {
        string root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "trackdub-pin", "Providers", "trt-rtx"));
        string userChosen = Path.GetFullPath(Path.Join(Path.GetTempPath(), "my-trt-rtx-build"));
        string current = Path.Join(root, TensorRtRtxProviderConstants.BundledVersion, TensorRtRtxProviderConstants.BundledCudaVariant, "win-x64");

        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            explicitPluginDirectory: userChosen,
            defaultInstallDirectory: current,
            getEnvironmentVariable: _ => null,
            directoryExists: _ => true,
            fileExists: path => path.StartsWith(current, StringComparison.OrdinalIgnoreCase));

        Assert.False(resolution.Succeeded);
        Assert.Equal(TensorRtRtxPluginDirectorySource.ExplicitStudioSetting, resolution.Source);
        Assert.Equal(userChosen, resolution.DirectoryPath);
    }

    [Fact]
    public void Runtime_cache_path_is_scoped_to_the_bundled_fingerprint()
    {
        string path = OnnxExecutionSessionFactory.ResolveTensorRtRtxRuntimeCachePath();

        Assert.Equal(TensorRtRtxProviderConstants.BundledFingerprintVersion, Path.GetFileName(path));
        Assert.Equal("trt-rtx", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData(@"0.3.0\cu12\win-x64", true)]
    [InlineData(@"0.4.2\cu12\win-x64", true)]
    [InlineData(@"0.4.2\cu13\win-x64", true)]
    [InlineData(@"0.4.0\cu13\linux-x64", true)]
    [InlineData(@"CURRENT", false)]
    public void IsSupersededManagedInstallDirectory_detects_other_versions_under_the_managed_root(string relative, bool expected)
    {
        string root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "trackdub-pin", "Providers", "trt-rtx"));
        string current = Path.Join(root, "0.5.0", "cu13", "win-x64");
        string candidate = relative == "CURRENT"
            ? current
            : Path.Join(root, relative.Replace('\\', Path.DirectorySeparatorChar));

        Assert.Equal(expected, TensorRtRtxProviderConstants.IsSupersededManagedInstallDirectory(candidate, current));
    }

    private static string ResolveRepoManifestPath()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Join(dir, "runtime", "trt-rtx-ep.manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate runtime/trt-rtx-ep.manifest.json from test output directory.");
    }
}
