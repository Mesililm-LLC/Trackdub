using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.Benchmarking;

/// <summary>
/// Device-bound I/O path for benchmark sampling on provider sessions that report device
/// memory for their inputs (TensorRT-RTX today). Host tensors stay pinned and bound;
/// <see cref="OrtIoBinding.SynchronizeBoundInputs"/> performs the host-to-device copy
/// each pass, and <see cref="OrtIoBinding.SynchronizeBoundOutputs"/> fences device work
/// after the bound run, so the measured pass covers device execution instead of
/// enqueue-only latency. Lifetime is owned: host tensors and the binding are disposed
/// with this object.
/// </summary>
public sealed class DeviceBoundIoBinding : IDisposable
{
    private readonly InferenceSession _session;
    private readonly OrtIoBinding _binding;
    private readonly RunOptions _runOptions;
    private readonly List<OrtValue> _hostValues;
    private bool _disposed;

    private DeviceBoundIoBinding(
        InferenceSession session,
        OrtIoBinding binding,
        List<OrtValue> hostValues)
    {
        _session = session;
        _binding = binding;
        _runOptions = new RunOptions();
        _hostValues = hostValues;
    }

    /// <summary>
    /// Build a device-bound binding for a single-graph session. Throws when the session
    /// reports host-resident inputs only or the tensor recipe is unsupported, so the
    /// caller never silently degrades to managed I/O.
    /// </summary>
    public static DeviceBoundIoBinding Create(
        InferenceSession session,
        IReadOnlyList<NamedOnnxValue> inputs,
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata,
        IReadOnlyDictionary<string, NodeMetadata> outputMetadata,
        ICollection<string> notes,
        ICollection<string> limitations)
    {
        IReadOnlyList<OrtMemoryInfo> inputMemoryInfos = session.GetMemoryInfosForInputs();
        IReadOnlyList<OrtMemoryInfo> outputMemoryInfos = session.GetMemoryInfosForOutputs();

        var deviceMemoryInfos = inputMemoryInfos.Where(IsDeviceMemory).ToList();
        if (deviceMemoryInfos.Count == 0)
        {
            throw new NotSupportedException(
                "Session reports host-resident inputs only; device-bound I/O timing is unavailable.");
        }

        OrtIoBinding binding = session.CreateIoBinding();
        var hostValues = new List<OrtValue>();

        try
        {
            for (int index = 0; index < inputs.Count; index++)
            {
                NamedOnnxValue input = inputs[index];
                NodeMetadata meta = inputMetadata[input.Name];
                OrtMemoryInfo inputInfo = index < inputMemoryInfos.Count
                    ? inputMemoryInfos[index]
                    : OrtMemoryInfo.DefaultInstance;

                OrtValue host = CreateHostValue(input, meta.ElementType);
                hostValues.Add(host);
                binding.BindInput(input.Name, host);
                if (!IsDeviceMemory(inputInfo))
                {
                    limitations.Add($"input '{input.Name}' is host-resident per session memory info");
                }
            }

            for (int index = 0; index < outputMetadata.Count; index++)
            {
                KeyValuePair<string, NodeMetadata> output = outputMetadata.ElementAt(index);
                OrtMemoryInfo outputInfo = index < outputMemoryInfos.Count
                    ? outputMemoryInfos[index]
                    : OrtMemoryInfo.DefaultInstance;
                binding.BindOutputToDevice(output.Key, outputInfo);
                if (!IsDeviceMemory(outputInfo))
                {
                    limitations.Add($"output '{output.Key}' is host-resident per session memory info");
                }
            }
        }
        catch
        {
            foreach (OrtValue value in hostValues) value.Dispose();
            binding.Dispose();
            throw;
        }

        notes.Add(
            "Device-bound I/O: inputs pinned and copied by bound-input synchronization; "
            + "outputs bound to session-reported memory.");
        return new DeviceBoundIoBinding(session, binding, hostValues);
    }

    /// <summary>
    /// One measured pass: copy bound inputs to device, run with the binding, then fence
    /// output readiness so the wall-clock sample covers device work.
    /// </summary>
    public void RunPass()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _binding.SynchronizeBoundInputs();
        _session.RunWithBinding(_runOptions, _binding);
        _binding.SynchronizeBoundOutputs();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (OrtValue value in _hostValues) value.Dispose();
        _binding.Dispose();
        _runOptions.Dispose();
    }

    private static bool IsDeviceMemory(OrtMemoryInfo info) =>
        !info.Equals(OrtMemoryInfo.DefaultInstance);

    private static OrtValue CreateHostValue(NamedOnnxValue input, Type elementType)
    {
        if (elementType == typeof(float)) return CreateHostValue<float>(input);
        if (elementType == typeof(Half)) return CreateHostValue<Half>(input);
        if (elementType == typeof(int)) return CreateHostValue<int>(input);
        if (elementType == typeof(long)) return CreateHostValue<long>(input);
        throw new NotSupportedException(
            $"Device-bound I/O does not support '{elementType.Name}' inputs in this build.");
    }

    private static OrtValue CreateHostValue<T>(NamedOnnxValue input) where T : unmanaged
    {
        if (input.AsTensor<T>() is not DenseTensor<T> tensor)
        {
            throw new NotSupportedException(
                $"Device-bound I/O requires dense '{typeof(T).Name}' input tensors.");
        }
        long[] shape = Array.ConvertAll(tensor.Dimensions.ToArray(), dim => (long)dim);
        return OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, tensor.Buffer, shape);
    }
}
