using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Dnnl;
using Trackdub.Inference.Onnx.EpContext;
using Trackdub.Inference.Onnx.ExecutionProviders;
using Trackdub.Inference.Onnx.Pool;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif
using Trackdub.Inference.Onnx.Migraphx;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.Migraphx;
using Trackdub.Inference.Runtime.TensorRtRtx;
using Trackdub.Inference.Runtime.WinMlCatalog;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx;

internal static partial class OnnxExecutionSessionFactory
{
    internal static ExecutionProviderKind ResolveCatalogEffectiveProviderFromSession(InferenceSession session)
    {
        IReadOnlyList<OrtEpDevice> devices;
        try
        {
            devices = session.GetEpDeviceForInputs();
        }
        catch (OnnxRuntimeException)
        {
            return ExecutionProviderKind.Cpu;
        }

        if (devices.Count == 0)
        {
            return ExecutionProviderKind.Cpu;
        }

        ExecutionProviderKind? bestProvider = null;
        int bestRank = int.MaxValue;
        foreach (OrtEpDevice device in devices)
        {
            if (!TryMapCatalogEpNameToExecutionProviderKind(device.EpName, out ExecutionProviderKind mappedProvider))
            {
                ReportUnmappedCatalogEpName(device.EpName, CatalogEpNameSource.GetEpDeviceForInputs, devices.Count);
                continue;
            }

            int rank = GetCatalogEpHonestyRank(mappedProvider, device.HardwareDevice.Type);
            if (rank >= bestRank)
            {
                continue;
            }

            bestRank = rank;
            bestProvider = mappedProvider;
        }

        return bestProvider ?? ExecutionProviderKind.Cpu;
    }

    internal static bool TryMapCatalogEpNameToExecutionProviderKind(
        string epName,
        out ExecutionProviderKind executionProviderKind)
    {
        if (IsTensorRtRtxDeviceCandidate(epName, OrtHardwareDeviceType.GPU))
        {
            executionProviderKind = ExecutionProviderKind.TensorRTRtx;
            return true;
        }

        if (string.Equals(epName, MigraphxProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.Migraphx;
            return true;
        }

        if (IsDnnlExecutionProviderName(epName))
        {
            executionProviderKind = ExecutionProviderKind.Dnnl;
            return true;
        }

        if (IsDirectMlDeviceCandidate(epName, OrtHardwareDeviceType.GPU))
        {
            executionProviderKind = ExecutionProviderKind.DirectMl;
            return true;
        }

        if (string.Equals(epName, OpenVinoCatalogProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.OpenVinoCatalog;
            return true;
        }

        if (string.Equals(epName, QnnProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.Qnn;
            return true;
        }

        if (string.Equals(epName, VitisAiProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.VitisAi;
            return true;
        }

        if (string.Equals(epName, "CPUExecutionProvider", StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.Cpu;
            return true;
        }

        executionProviderKind = default;
        return false;
    }

    internal enum CatalogEpNameSource
    {
        GetEpDeviceForInputs,
        GetEpDevices
    }

    internal static void ReportUnmappedCatalogEpName(
        string epName,
        CatalogEpNameSource source,
        int deviceCount)
    {
        if (string.IsNullOrWhiteSpace(epName))
        {
            return;
        }

        if (!WarnedUnmappedCatalogEpNames.TryAdd(epName, 0))
        {
            return;
        }

        _logger?.LogWarning(
            "Unmapped Windows ML catalog EP name '{EpName}' encountered ({Source}, deviceCount={DeviceCount}); treating as CPU only for honesty resolution. Add a mapping in TryMapCatalogEpNameToExecutionProviderKind if this EP is supported. See docs/internal/windows-ml-phase-5-catalog-eps.md.",
            epName,
            source,
            deviceCount);
    }

    private static int GetCatalogEpHonestyRank(ExecutionProviderKind provider, OrtHardwareDeviceType deviceType) =>
        (provider, deviceType) switch
        {
            (ExecutionProviderKind.TensorRTRtx, _) => 0,
            (ExecutionProviderKind.Migraphx, _) => 1,
            (ExecutionProviderKind.Qnn, OrtHardwareDeviceType.NPU) => 2,
            (ExecutionProviderKind.OpenVinoCatalog, OrtHardwareDeviceType.NPU) => 3,
            (ExecutionProviderKind.VitisAi, _) => 4,
            (ExecutionProviderKind.DirectMl, _) => 5,
            (ExecutionProviderKind.Qnn, OrtHardwareDeviceType.GPU) => 6,
            (ExecutionProviderKind.OpenVinoCatalog, OrtHardwareDeviceType.GPU) => 7,
            (ExecutionProviderKind.OpenVinoCatalog, OrtHardwareDeviceType.CPU) => 8,
            (ExecutionProviderKind.Dnnl, OrtHardwareDeviceType.CPU) => 9,
            (ExecutionProviderKind.Cpu, _) => 10,
            _ => 11
        };
}
