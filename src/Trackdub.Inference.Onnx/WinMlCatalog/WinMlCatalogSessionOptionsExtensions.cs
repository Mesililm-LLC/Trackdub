using Microsoft.ML.OnnxRuntime;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.WinMlCatalog;

namespace Trackdub.Inference.Onnx.WinMlCatalog;

internal static class WinMlCatalogSessionOptionsExtensions
{
    public static bool TryAppendOpenVinoCatalogProvider(SessionOptions options, out string? failureReason) =>
        TryAppendCatalogProvider(
            options,
            OpenVinoCatalogProviderConstants.OrtExecutionProviderName,
            preferredDeviceTypes: [OrtHardwareDeviceType.NPU, OrtHardwareDeviceType.GPU, OrtHardwareDeviceType.CPU],
            out failureReason);

    public static ExecutionProviderKind AppendOpenVinoCatalogOrFallback(SessionOptions options) =>
        TryAppendOpenVinoCatalogProvider(options, out _)
            ? ExecutionProviderKind.OpenVinoCatalog
            : ExecutionProviderKind.Cpu;

    public static bool TryAppendQnnProvider(SessionOptions options, out string? failureReason) =>
        TryAppendCatalogProvider(
            options,
            QnnProviderConstants.OrtExecutionProviderName,
            preferredDeviceTypes: [OrtHardwareDeviceType.NPU, OrtHardwareDeviceType.GPU],
            out failureReason);

    public static ExecutionProviderKind AppendQnnOrFallback(SessionOptions options) =>
        TryAppendQnnProvider(options, out _) ? ExecutionProviderKind.Qnn : ExecutionProviderKind.Cpu;

    public static bool TryAppendVitisAiProvider(SessionOptions options, out string? failureReason) =>
        TryAppendCatalogProvider(
            options,
            VitisAiProviderConstants.OrtExecutionProviderName,
            preferredDeviceTypes: [OrtHardwareDeviceType.NPU],
            out failureReason);

    public static ExecutionProviderKind AppendVitisAiOrFallback(SessionOptions options) =>
        TryAppendVitisAiProvider(options, out _) ? ExecutionProviderKind.VitisAi : ExecutionProviderKind.Cpu;

#if WINDOWS
    public static bool TryRegisterInstalledQnnProvider(out string? failureReason)
    {
        failureReason = null;
        try
        {
            Microsoft.Windows.AI.MachineLearning.ExecutionProvider? provider =
                Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault()
                    .FindAllProviders()
                    .FirstOrDefault(candidate => string.Equals(
                        candidate.Name,
                        QnnProviderConstants.OrtExecutionProviderName,
                        StringComparison.Ordinal));
            if (provider is null)
            {
                failureReason = $"{QnnProviderConstants.OrtExecutionProviderName} is not installed.";
                return false;
            }

            if (!provider.TryRegister())
            {
                failureReason = $"TryRegister failed for {QnnProviderConstants.OrtExecutionProviderName}.";
                return false;
            }

            if (!QnnOrtProbe.IsProviderListed())
            {
                failureReason = "QNN registered with the catalog but is not listed by ONNX Runtime.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }
    }
#endif

    private static bool TryAppendCatalogProvider(
        SessionOptions options,
        string ortExecutionProviderName,
        OrtHardwareDeviceType[] preferredDeviceTypes,
        out string? failureReason)
    {
        failureReason = null;

#if WINDOWS
        Trackdub.Inference.Onnx.WindowsMl.WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif

        try
        {
            IReadOnlyList<OrtEpDevice> devices = OrtEnv.Instance().GetEpDevices();
            foreach (OrtHardwareDeviceType deviceType in preferredDeviceTypes)
            {
                OrtEpDevice? device = devices.FirstOrDefault(candidate =>
                    string.Equals(candidate.EpName, ortExecutionProviderName, StringComparison.OrdinalIgnoreCase) &&
                    candidate.HardwareDevice.Type == deviceType);

                if (device is not null)
                {
                    options.AppendExecutionProvider(OrtEnv.Instance(), [device], null);
                    return true;
                }
            }

            if (devices.Any(candidate =>
                    string.Equals(candidate.EpName, ortExecutionProviderName, StringComparison.OrdinalIgnoreCase)))
            {
                options.AppendExecutionProvider(ortExecutionProviderName);
                return true;
            }

            failureReason = $"{ortExecutionProviderName} is not registered with ONNX Runtime.";
            return false;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            failureReason = ex.Message;
            return false;
        }
    }
}
