using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Trackdub.Inference.Onnx.TensorRtRtx;

#if WINDOWS
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace Trackdub.Inference.Onnx.Tests;

public sealed class TensorRtRtxCudaRuntimeBootstrapTests
{
    [Fact]
    public void DiscoverSearchDirectories_IncludesConfiguredDirectory()
    {
        string directory = Path.Join(Path.GetTempPath(), $"trackdub-cuda-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string? previous = Environment.GetEnvironmentVariable("TRACKDUB_CUDA_BIN_DIR");

        try
        {
            Environment.SetEnvironmentVariable("TRACKDUB_CUDA_BIN_DIR", directory);
            Assert.Contains(
                directory,
                TensorRtRtxCudaRuntimeBootstrap.DiscoverSearchDirectories(),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_CUDA_BIN_DIR", previous);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void DiscoverSearchDirectories_prefers_plugin_directory_first()
    {
        string pluginDirectory = Path.Join(Path.GetTempPath(), $"trackdub-trt-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginDirectory);

        try
        {
            Assert.Equal(
                pluginDirectory,
                TensorRtRtxCudaRuntimeBootstrap.DiscoverSearchDirectories(pluginDirectory).First(),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(pluginDirectory, recursive: true);
        }
    }

    [Fact]
    public void TryEnsureLoadedResult_on_windows_needs_no_cuda_runtime_library()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The Windows cu13 TensorRT-RTX binaries (1.6, and 1.7 in EP ABI 0.5.0) import no cudart DLL (static CUDA runtime).
        Assert.Null(TensorRtRtxCudaRuntimeBootstrap.RequiredCudaRuntimeFileName);
        TensorRtRtxCudaRuntimeEnsureResult result = TensorRtRtxCudaRuntimeBootstrap.TryEnsureLoadedResult();

        Assert.True(result.Succeeded);
        Assert.Null(result.LoadedPath);
        Assert.Contains("statically linked", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TryEnsureLoadedResult_without_cuda13_in_search_path_reports_missing_runtime()
    {
        string emptyDir = Path.Join(Path.GetTempPath(), $"trackdub-cuda-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);

        try
        {
            TensorRtRtxCudaRuntimeEnsureResult result =
                TensorRtRtxCudaRuntimeBootstrap.TryEnsureLoadedResult(
                    [emptyDir],
                    TensorRtRtxCudaRuntimeBootstrap.LinuxCudaRuntimeFileName);

            Assert.False(result.Succeeded);
            Assert.Null(result.LoadedPath);
            Assert.Contains("CUDA 13 runtime", result.Detail, StringComparison.Ordinal);
            Assert.Contains("cu13", result.Detail, StringComparison.Ordinal);
            Assert.Contains("TRACKDUB_CUDA_BIN_DIR", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(emptyDir))
            {
                Directory.Delete(emptyDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DescribeInstalledCudaMajorVersions_is_never_empty()
    {
        string detail = TensorRtRtxCudaRuntimeBootstrap.DescribeInstalledCudaMajorVersions();
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Fact]
    public void EnumerateChildDirectoriesSafe_SurvivesUnreadableRoot()
    {
        string root = Path.Join(Path.GetTempPath(), $"trackdub-cuda-blocked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(root, "child"));

        try
        {
            if (!TryDenyDirectoryEnumerateAccess(root))
            {
                return;
            }

            // Must not throw when Directory.EnumerateDirectories fails on a locked root.
            string[] children = TensorRtRtxCudaRuntimeBootstrap
                .EnumerateChildDirectoriesSafe(root)
                .ToArray();

            Assert.Empty(children);
        }
        finally
        {
            RestoreDirectoryAccess(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool TryDenyDirectoryEnumerateAccess(string directory)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            DenyWindowsDirectoryEnumerateAccess(directory);
            return true;
        }
#endif

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
            return true;
        }

        return false;
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private static void DenyWindowsDirectoryEnumerateAccess(string directory)
    {
        DirectoryInfo info = new(directory);
        DirectorySecurity security = info.GetAccessControl();
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(currentUser);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.ListDirectory | FileSystemRights.Read | FileSystemRights.ReadData,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }
#endif

    private static void RestoreDirectoryAccess(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                DirectoryInfo info = new(directory);
                DirectorySecurity security = info.GetAccessControl();
                AuthorizationRuleCollection rules = security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: false,
                    typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules.OfType<FileSystemAccessRule>())
                {
                    if (rule.AccessControlType == AccessControlType.Deny)
                    {
                        security.RemoveAccessRule(rule);
                    }
                }

                info.SetAccessControl(security);
                return;
            }
#endif

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch
        {
            // Best-effort cleanup so temp delete can proceed.
        }
    }
}
