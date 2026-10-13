using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.ExecutionProviders;

namespace Trackdub.Benchmarks;

/// <summary>
/// Shared ONNX benchmark harness bootstrap for <see cref="Program"/> and DubBench.
/// </summary>
public static class BenchmarkOnnxExecutionBootstrap
{
    public static void ConfigureExecution(BenchmarkOptions options)
    {
        ConfigureExecution(options.WindowsMlDevicePolicyKey);
#if WINDOWS
        if (options.ProviderPreference is BenchmarkProviderPreference.Qnn
            && !Trackdub.Inference.Onnx.WinMlCatalog.QnnBenchmarkRegistration
                .TryRegisterInstalledProvider(out string? failureReason))
        {
            throw new InvalidOperationException(
                $"QNN execution provider could not be registered from the installed catalog; " +
                $"install QNN before running the benchmark. {failureReason}");
        }
#endif
    }

    public static void ConfigureExecution(string? windowsMlDevicePolicyKey)
    {
#if WINDOWS
        WindowsMlExecutionDevicePolicy policy = string.IsNullOrWhiteSpace(windowsMlDevicePolicyKey)
            ? WindowsMlExecutionDevicePolicy.Explicit
            : WindowsMlExecutionDevicePolicySettings.FromKey(windowsMlDevicePolicyKey);

        OnnxExecutionProviderBootstrapperRegistry.Initialize(
            new Trackdub.Inference.Onnx.ExecutionProviders.Windows.WindowsExecutionProviderBootstrapper(),
            new Trackdub.Inference.Onnx.WindowsMl.FixedWindowsMlEpDevicePolicyProvider(policy));
#endif
    }

    public static IModelBenchmarkRunner? CreateOnnxRunner()
    {
#if WINDOWS
        return new OnnxModelBenchmarkRunner(BenchmarkTensorRtRtxBootstrap.Create());
#else
        return null;
#endif
    }
}
