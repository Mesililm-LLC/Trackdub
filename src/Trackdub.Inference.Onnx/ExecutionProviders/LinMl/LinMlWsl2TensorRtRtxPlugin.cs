using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LinML.Catalog;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

public enum LinMlWsl2PluginStatus
{
    /// <summary>Not WSL2, or LinML's WSL2 build is not vendored: use the catalog's plugin.</summary>
    NotApplicable,

    /// <summary>The WSL2 build is registered and exposes at least one device.</summary>
    Registered,

    /// <summary>The WSL2 build is vendored but the stock plugin's runtime libraries it links are not.</summary>
    StockMissing,

    /// <summary>The WSL2 build is vendored but could not be used.</summary>
    Failed,
}

public sealed record LinMlWsl2PluginResult(LinMlWsl2PluginStatus Status, string Detail, string? Hint = null);

/// <summary>
/// Registers LinML's patched TensorRT-RTX plugin on WSL2. The stock plugin registers there but
/// exposes no device (ONNX Runtime reports the GPU on a virtual PCI bus that
/// <c>cudaDeviceGetByPCIBusId</c> rejects), so the catalog's pinned plugin cannot serve WSL2. LinML
/// publishes a patched build as an opt-in release (<c>scripts/fetch-ep-plugins.sh trt-rtx-wsl2</c>,
/// upstream NVIDIA/TensorRT-RTX-EP-ABI PR #12); this uses it when it is vendored and never
/// downloads it. It must register before the catalog does, because ORT accepts one registration
/// per EP name.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinMlWsl2TensorRtRtxPlugin
{
    internal const string InstallHint =
        "On WSL2 the stock TensorRT-RTX plugin exposes no device. Vendor LinML's WSL2 build with "
        + "`bash scripts/fetch-ep-plugins.sh trt-rtx-wsl2` (LinML release ep-trt-rtx-wsl2-0.4.0-wsl2p1), then restart.";

    internal const string StockMissingHint =
        "Restart after the stock TensorRT-RTX plugin is installed so LinML's WSL2 build registers first.";

    private const string RegistrationName = TensorRtRtxProviderConstants.PluginOrtExecutionProviderName;

    // Registration is process-wide and ORT accepts one registration per EP name, so the
    // check-then-register sequence must not interleave across concurrent bootstraps.
    private static readonly object RegistrationGate = new();

    // Dependency handles stay loaded for the life of the process, as ORT may resolve the plugin lazily.
    private static readonly List<IntPtr> LoadedDependencies = [];

    private readonly string _installRoot;
    private readonly Func<bool> _isWsl;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly Action<string, string> _register;
    private readonly Func<int> _deviceCount;
    private readonly Action<string, string> _loadDependencies;

    public LinMlWsl2TensorRtRtxPlugin(
        string installRoot,
        Func<bool> isWsl,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Action<string, string> register,
        Func<int> deviceCount,
        Action<string, string> loadDependencies)
    {
        _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
        _isWsl = isWsl ?? throw new ArgumentNullException(nameof(isWsl));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        _register = register ?? throw new ArgumentNullException(nameof(register));
        _deviceCount = deviceCount ?? throw new ArgumentNullException(nameof(deviceCount));
        _loadDependencies = loadDependencies ?? throw new ArgumentNullException(nameof(loadDependencies));
    }

    public static LinMlWsl2TensorRtRtxPlugin CreateDefault() =>
        new(
            ExecutionProviderCatalog.DefaultInstallRoot,
            LinuxWsl.IsWsl,
            File.Exists,
            Directory.Exists,
            PluginLibraryRegistry.Register,
            static () => PluginLibraryRegistry.GetDevices(RegistrationName).Count,
            LoadStockDependencies);

    internal string PatchedLibraryPath => Path.Join(
        _installRoot, "ep-plugins", "trt-rtx-wsl2", "linml_trt_rtx_ep", TensorRtRtxProviderConstants.PluginLibraryFileNameLinux);

    // The catalog's stock plugin directory: the patched build links its TensorRT-RTX and CUDA
    // libraries by SONAME and ships none of them.
    internal string StockDirectory => Path.Join(
        _installRoot, "ep-plugins", "trt-rtx", "onnxruntime_ep_nv_tensorrt_rtx");

    public LinMlWsl2PluginResult TryRegister()
    {
        if (!_isWsl())
        {
            return new(LinMlWsl2PluginStatus.NotApplicable, "Not running under WSL.");
        }

        string patched = PatchedLibraryPath;
        if (!_fileExists(patched))
        {
            return new(LinMlWsl2PluginStatus.NotApplicable, "LinML's WSL2 TensorRT-RTX build is not vendored.", InstallHint);
        }

        lock (RegistrationGate)
        {
            if (_deviceCount() > 0)
            {
                return new(LinMlWsl2PluginStatus.Registered, $"TensorRT-RTX WSL2 plugin already registered ({patched}).");
            }

            if (!_directoryExists(StockDirectory))
            {
                return new(
                    LinMlWsl2PluginStatus.StockMissing,
                    $"LinML's WSL2 TensorRT-RTX build links the stock plugin's runtime libraries, which are not vendored at {StockDirectory}.",
                    StockMissingHint);
            }

            try
            {
                _loadDependencies(StockDirectory, TensorRtRtxProviderConstants.PluginLibraryFileNameLinux);
                _register(RegistrationName, patched);
            }
            catch (ArgumentException ex)
            {
                // Another registration of this EP name won the race, or the catalog registered the stock plugin earlier.
                int existing = _deviceCount();
                if (existing > 0)
                {
                    return new(LinMlWsl2PluginStatus.Registered, $"TensorRT-RTX WSL2 plugin already registered ({existing} EP device(s)).");
                }

                return new(
                    LinMlWsl2PluginStatus.Failed,
                    $"The stock TensorRT-RTX plugin was already registered in this process, so the WSL2 build cannot replace it. "
                    + $"Restart to use {patched}. ({ex.Message})");
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or FileNotFoundException or DllNotFoundException or BadImageFormatException)
            {
                return new(LinMlWsl2PluginStatus.Failed, $"Registering {patched} failed: {ex.Message}");
            }

            int devices = _deviceCount();
            return devices > 0
                ? new(LinMlWsl2PluginStatus.Registered, $"TensorRT-RTX WSL2 plugin registered from {patched} ({devices} EP device(s)).")
                : new(LinMlWsl2PluginStatus.Failed, $"{patched} registered but exposes no EP devices. Check that nvidia-smi sees the GPU in WSL.");
        }
    }

    // Loads the stock plugin's bundled libraries by path so the patched build's SONAME
    // dependencies (libcudart, libtensorrt_rtx, the ONNX parser) resolve without LD_LIBRARY_PATH.
    private static void LoadStockDependencies(string directory, string pluginFileName)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.so*"))
        {
            if (string.Equals(Path.GetFileName(path), pluginFileName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                IntPtr handle = NativeLibrary.Load(path);
                lock (LoadedDependencies)
                {
                    LoadedDependencies.Add(handle);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                // A bundled library may depend on a host driver library; registration reports it.
            }
        }
    }
}
