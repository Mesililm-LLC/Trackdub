using System.Runtime.CompilerServices;
using Trackdub.Inference.Onnx.Worker;

namespace Trackdub.Sdk.Tests;

internal static class TestProcessEnvironment
{
    /// <summary>
    /// The Windows build copies the inference worker next to the test binaries, so on NVIDIA
    /// machines every composition's provider discovery started a real worker process to ask
    /// about CUDA (~0.5 s per project open). No SDK test exercises the worker; pointing its path
    /// at a missing file makes discovery treat it as not installed, as on CI runners.
    /// </summary>
    [ModuleInitializer]
    internal static void DisableInferenceWorker()
    {
        if (Environment.GetEnvironmentVariable(InferenceWorkerClient.WorkerPathEnvironmentVariable) is null)
        {
            Environment.SetEnvironmentVariable(
                InferenceWorkerClient.WorkerPathEnvironmentVariable,
                Path.Join(AppContext.BaseDirectory, "inference-worker-disabled-for-tests", "missing.exe"));
        }
    }
}
