using System.Runtime.Versioning;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
using Trackdub.Inference.Onnx.Runtime.Planning;

namespace Trackdub.Inference.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinMlWsl2TensorRtRtxPluginTests
{
    private const string Root = "/home/u/.local/linml-deps";

    [Fact]
    public void Outside_WSL_nothing_is_touched()
    {
        var calls = new Calls();
        LinMlWsl2TensorRtRtxPlugin plugin = Make(calls, isWsl: false);

        LinMlWsl2PluginResult result = plugin.TryRegister();

        Assert.Equal(LinMlWsl2PluginStatus.NotApplicable, result.Status);
        Assert.Empty(calls.Registered);
        Assert.Empty(calls.LoadedFrom);
    }

    [Fact]
    public void Without_the_vendored_WSL2_build_the_catalog_plugin_is_used_and_the_hint_is_offered()
    {
        LinMlWsl2PluginResult result = Make(new Calls(), patchedPresent: false).TryRegister();

        Assert.Equal(LinMlWsl2PluginStatus.NotApplicable, result.Status);
        Assert.Contains("trt-rtx-wsl2", result.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_WSL2_build_registers_after_loading_the_stock_plugins_libraries()
    {
        var calls = new Calls { DevicesAfterRegister = 1 };
        LinMlWsl2TensorRtRtxPlugin plugin = Make(calls);

        LinMlWsl2PluginResult result = plugin.TryRegister();

        Assert.Equal(LinMlWsl2PluginStatus.Registered, result.Status);
        Assert.Equal([plugin.StockDirectory], calls.LoadedFrom);
        Assert.Equal([("NvTensorRTRTXExecutionProvider", plugin.PatchedLibraryPath)], calls.Registered);
        Assert.True(calls.LoadedBeforeRegister);
    }

    [Fact]
    public void An_already_registered_build_is_not_registered_again()
    {
        var calls = new Calls { DevicesBefore = 1 };

        LinMlWsl2PluginResult result = Make(calls).TryRegister();

        Assert.Equal(LinMlWsl2PluginStatus.Registered, result.Status);
        Assert.Empty(calls.Registered);
    }

    [Fact]
    public void Missing_stock_libraries_fail_with_a_restart_instruction()
    {
        LinMlWsl2PluginResult result = Make(new Calls(), stockDirectoryPresent: false).TryRegister();

        Assert.Equal(LinMlWsl2PluginStatus.Failed, result.Status);
        Assert.Contains("restart", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stock_plugin_registered_earlier_in_the_process_asks_for_a_restart()
    {
        var calls = new Calls { RegisterThrows = new ArgumentException("already registered with a different path") };

        LinMlWsl2PluginResult result = Make(calls).TryRegister();

        Assert.Equal(LinMlWsl2PluginStatus.Failed, result.Status);
        Assert.Contains("Restart", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_build_that_registers_without_devices_fails()
    {
        var calls = new Calls { DevicesAfterRegister = 0 };

        LinMlWsl2PluginResult result = Make(calls).TryRegister();

        Assert.Equal(LinMlWsl2PluginStatus.Failed, result.Status);
        Assert.Contains("no EP devices", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Wsl_detection_reads_the_interop_file_or_the_kernel_version()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.True(LinuxWsl.IsWsl(static p => p.EndsWith("WSLInterop", StringComparison.Ordinal), static _ => string.Empty));
        Assert.True(LinuxWsl.IsWsl(static p => p == "/proc/version", static _ => "Linux 6.1 microsoft-standard-WSL2"));
        Assert.False(LinuxWsl.IsWsl(static p => p == "/proc/version", static _ => "Linux 6.8 generic"));
    }

    [Fact]
    public void Gpu_passthrough_needs_the_dxg_device_and_the_projected_cuda_library()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.True(LinuxWsl.HasNvidiaGpuPassthrough(static _ => true));
        Assert.False(LinuxWsl.HasNvidiaGpuPassthrough(static p => p == "/dev/dxg"));
        Assert.False(LinuxWsl.HasNvidiaGpuPassthrough(static p => p.EndsWith("libcuda.so.1", StringComparison.Ordinal)));
    }

    internal static LinMlWsl2TensorRtRtxPlugin NotWsl() => Make(new Calls(), isWsl: false);

    internal static LinMlWsl2TensorRtRtxPlugin Wsl(int devices, bool patchedPresent = true, bool stockDirectoryPresent = true) =>
        Make(
            new Calls { DevicesBefore = devices },
            patchedPresent: patchedPresent,
            stockDirectoryPresent: stockDirectoryPresent);

    private static LinMlWsl2TensorRtRtxPlugin Make(
        Calls calls,
        bool isWsl = true,
        bool patchedPresent = true,
        bool stockDirectoryPresent = true) =>
        new(
            Root,
            () => isWsl,
            path => patchedPresent && path.EndsWith("libonnxruntime_providers_nv_tensorrt_rtx.so", StringComparison.Ordinal),
            _ => stockDirectoryPresent,
            calls.Register,
            calls.DeviceCount,
            calls.Load);

    private sealed class Calls
    {
        public int DevicesBefore { get; init; }

        public int DevicesAfterRegister { get; init; }

        public Exception? RegisterThrows { get; init; }

        public List<(string Name, string Path)> Registered { get; } = [];

        public List<string> LoadedFrom { get; } = [];

        public bool LoadedBeforeRegister { get; private set; }

        public void Load(string directory, string pluginFileName) => LoadedFrom.Add(directory);

        public void Register(string name, string path)
        {
            LoadedBeforeRegister = LoadedFrom.Count > 0;
            if (RegisterThrows is not null)
            {
                throw RegisterThrows;
            }

            Registered.Add((name, path));
        }

        public int DeviceCount() => Registered.Count > 0 ? DevicesAfterRegister : DevicesBefore;
    }
}
