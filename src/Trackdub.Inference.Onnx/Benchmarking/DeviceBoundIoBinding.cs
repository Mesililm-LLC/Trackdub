using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.Benchmarking;

/// <summary>
/// Device-bound I/O path for benchmark sampling on provider sessions that report device
/// memory for their inputs (TensorRT-RTX today). On TRT-RTX,
/// <see cref="OrtIoBinding.SynchronizeBoundInputs"/> is a no-op for host-bound OrtValues —
/// the engine runs on its own fixed device buffers — so inputs are staged in pinned host
/// OrtValues and copied host-to-device with <see cref="OrtEnv.CopyTensors"/> into bound
/// device OrtValues each pass, the same pattern production engines use. Outputs bind to
/// session-reported memory and <see cref="OrtIoBinding.SynchronizeBoundOutputs"/> fences
/// device work after the run. The measured pass therefore covers the real H2D copy plus
/// device execution. Lifetime is owned: host and device tensors and the binding are
/// disposed with this object.
/// </summary>
public sealed class DeviceBoundIoBinding : IDisposable
{
    private readonly InferenceSession _session;
    private readonly OrtIoBinding _binding;
    private readonly RunOptions _runOptions;
    private readonly OrtValue[] _hostValues;
    private readonly OrtValue[] _deviceValues;
    private bool _disposed;

    private DeviceBoundIoBinding(
        InferenceSession session,
        OrtIoBinding binding,
        OrtValue[] hostValues,
        OrtValue[] deviceValues)
    {
        _session = session;
        _binding = binding;
        _runOptions = new RunOptions();
        _hostValues = hostValues;
        _deviceValues = deviceValues;
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

        OrtAllocator deviceAllocator = OrtEnv.Instance().GetSharedAllocator(deviceMemoryInfos[0]);
        if (deviceAllocator.IsInvalid)
        {
            throw new NotSupportedException(
                $"No shared allocator registered for '{deviceMemoryInfos[0].Name}'.");
        }

        OrtIoBinding binding = session.CreateIoBinding();
        var hostValues = new List<OrtValue>();
        var deviceValues = new List<OrtValue>();

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
                OrtValue device = OrtValue.CreateAllocatedTensorValue(
                    deviceAllocator, ToTensorElementType(meta.ElementType),
                    host.GetTensorTypeAndShape().Shape);
                deviceValues.Add(device);
                binding.BindInput(input.Name, device);
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
            foreach (OrtValue value in deviceValues) value.Dispose();
            binding.Dispose();
            throw;
        }

        notes.Add(
            "Device-bound I/O: inputs staged on host and copied with OrtEnv.CopyTensors; "
            + "outputs bound to session-reported memory.");
        return new DeviceBoundIoBinding(
            session, binding, hostValues.ToArray(), deviceValues.ToArray());
    }

    /// <summary>
    /// One measured pass: copy staged inputs host-to-device, run with the binding, then
    /// fence output readiness so the wall-clock sample covers device work.
    /// </summary>
    public void RunPass()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        OrtEnv.Instance().CopyTensors(_hostValues, _deviceValues, default);
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
        foreach (OrtValue value in _deviceValues) value.Dispose();
        _binding.Dispose();
        _runOptions.Dispose();
    }

    private static bool IsDeviceMemory(OrtMemoryInfo info) =>
        !info.Equals(OrtMemoryInfo.DefaultInstance);

    internal static OrtValue CreateHostValue(NamedOnnxValue input, Type elementType)
    {
        if (elementType == typeof(float)) return CreateHostValue<float>(input);
        // ORT fp16 tensors are Float16; System.Half appears in metadata for some callers.
        // ORT only accepts Float16 in CreateTensorValueFromMemory, so Half inputs are
        // bit-reinterpreted into a Float16 buffer (identical IEEE-754 binary16 layout).
        if (elementType == typeof(Float16) || elementType == typeof(Half))
        {
            return CreateHostValueF16(input);
        }
        if (elementType == typeof(int)) return CreateHostValue<int>(input);
        if (elementType == typeof(long)) return CreateHostValue<long>(input);
        throw new NotSupportedException(
            $"Device-bound I/O does not support '{elementType.Name}' inputs in this build.");
    }

    private static OrtValue CreateHostValueF16(NamedOnnxValue input)
    {
        if (input.AsTensor<Float16>() is DenseTensor<Float16> tensor)
        {
            return CreateHostValue(tensor);
        }
        if (input.AsTensor<Half>() is DenseTensor<Half> halfTensor)
        {
            Float16[] data = MemoryMarshal.Cast<Half, Float16>(halfTensor.Buffer.Span).ToArray();
            return CreateHostValue(new DenseTensor<Float16>(data, halfTensor.Dimensions.ToArray()));
        }
        throw new NotSupportedException(
            "Device-bound I/O requires dense 'Float16' input tensors.");
    }

    private static OrtValue CreateHostValue<T>(NamedOnnxValue input) where T : unmanaged
    {
        if (input.AsTensor<T>() is not DenseTensor<T> tensor)
        {
            throw new NotSupportedException(
                $"Device-bound I/O requires dense '{typeof(T).Name}' input tensors.");
        }
        return CreateHostValue(tensor);
    }

    private static OrtValue CreateHostValue<T>(DenseTensor<T> tensor) where T : unmanaged
    {
        long[] shape = Array.ConvertAll(tensor.Dimensions.ToArray(), dim => (long)dim);
        return OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, tensor.Buffer, shape);
    }

    private static TensorElementType ToTensorElementType(Type elementType) =>
        elementType == typeof(float) ? TensorElementType.Float
        : elementType == typeof(Float16) || elementType == typeof(Half) ? TensorElementType.Float16
        : elementType == typeof(int) ? TensorElementType.Int32
        : elementType == typeof(long) ? TensorElementType.Int64
        : throw new NotSupportedException(
            $"Device-bound I/O does not support '{elementType.Name}' inputs in this build.");
}
