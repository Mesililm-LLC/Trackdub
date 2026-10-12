#if LINML
using LinML.Contracts;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
#endif
using Trackdub.Inference.Onnx.Migraphx;

namespace Trackdub.Inference.Onnx.Runtime.Planning;

public interface ILinuxNativeGpuRuntimeProbe
{
    bool IsNvidiaDriverLoaded();

    bool IsAmdGpuPresent();

    bool IsNativeTensorRtAvailable();

    bool IsCudaOrtProviderAvailable();

    bool IsMigraphxOrtProviderAvailable();
}

public sealed class LinuxNativeGpuRuntimeProbe : ILinuxNativeGpuRuntimeProbe
{
    // WSL2 exposes the GPU through /dev/dxg instead of /proc/driver/nvidia.
    public bool IsNvidiaDriverLoaded() =>
        Directory.Exists("/proc/driver/nvidia/gpus") || LinuxWsl.HasNvidiaGpuPassthrough();

    public bool IsAmdGpuPresent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            if (Directory.Exists("/sys/module/amdgpu"))
            {
                return true;
            }

            const string drmRoot = "/sys/class/drm";
            if (!Directory.Exists(drmRoot))
            {
                return false;
            }

            foreach (string cardPath in Directory.EnumerateDirectories(drmRoot, "card*"))
            {
                string vendorPath = Path.Join(cardPath, "device", "vendor");
                if (!File.Exists(vendorPath))
                {
                    continue;
                }

                string vendor = File.ReadAllText(vendorPath).Trim();
                if (vendor.Equals("0x1002", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    // On Linux the native TensorRT route needs both libnvinfer and a usable TensorrtExecutionProvider.
    public bool IsNativeTensorRtAvailable()
    {
        if (!NativeTensorRtLibraryProbe.IsNativeTensorRtAvailable())
        {
            return false;
        }

#if LINML
        return LinMlBuiltInProviders.IsReady(ProviderId.Tensorrt);
#else
        return true;
#endif
    }

    public bool IsCudaOrtProviderAvailable() =>
#if LINML
        LinMlBuiltInProviders.IsReady(ProviderId.Cuda);
#else
        CudaOrtProbe.IsCudaProviderListed();
#endif

    public bool IsMigraphxOrtProviderAvailable() =>
        MigraphxOrtProbe.IsProviderListed();
}
