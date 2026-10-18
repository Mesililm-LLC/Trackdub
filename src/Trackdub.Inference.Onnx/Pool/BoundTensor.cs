using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// A device-resident input tensor for providers whose sessions report device memory
/// (TensorRT-RTX). <c>SynchronizeBoundInputs</c> is a no-op for host-bound OrtValues
/// on TRT-RTX, so inputs are staged in <see cref="Backing"/>, then copied host-to-device with
/// <see cref="OrtEnv.CopyTensors"/> into <see cref="Device"/>, the OrtValue actually bound to
/// the session. The device allocation address is fixed for the object's lifetime, which is
/// the CUDA-graph replay precondition.
/// </summary>
internal sealed class DeviceBoundTensor<T> : IDisposable
    where T : unmanaged
{
    public DeviceBoundTensor(OrtAllocator allocator, long[] shape)
        : this(allocator, shape, checked((int)shape.Aggregate(1L, static (a, d) => a * d)))
    {
    }

    public DeviceBoundTensor(OrtAllocator allocator, long[] shape, int count)
    {
        Backing = new T[count];
        Host = OrtValue.CreateTensorValueFromMemory(Backing, shape);
        Device = OrtValue.CreateAllocatedTensorValue(allocator, ElementType, shape);
    }

    public static TensorElementType ElementType =>
        typeof(T) == typeof(float) ? TensorElementType.Float
        : typeof(T) == typeof(Half) ? TensorElementType.Float16
        : typeof(T) == typeof(int) ? TensorElementType.Int32
        : typeof(T) == typeof(long) ? TensorElementType.Int64
        : throw new NotSupportedException(typeof(T).Name);

    /// <summary>Host staging storage; update this then copy via <c>DeviceIoSync</c>.</summary>
    public T[] Backing { get; }

    /// <summary>Pinned host view of <see cref="Backing"/>, the source of each H2D copy.</summary>
    public OrtValue Host { get; }

    /// <summary>Device-resident OrtValue bound to the session.</summary>
    public OrtValue Device { get; }

    public void Dispose()
    {
        Host.Dispose();
        Device.Dispose();
    }
}

/// <summary>
/// A host-backed input/output tensor whose <see cref="OrtValue"/> wraps one fixed managed array for
/// the object's lifetime, so an <see cref="OrtIoBinding"/> sees the same addresses on every run.
/// CUDA graph replay requires exactly this: callers update <see cref="Backing"/> contents between
/// runs instead of creating fresh OrtValues.
/// </summary>
internal sealed class BoundTensor<T> : IDisposable
    where T : unmanaged
{
    public BoundTensor(long[] shape)
        : this(shape, checked((int)shape.Aggregate(1L, static (a, d) => a * d)))
    {
    }

    public BoundTensor(long[] shape, int count)
    {
        Backing = new T[count];
        Value = OrtValue.CreateTensorValueFromMemory(Backing, shape);
    }

    /// <summary>The managed storage the bound <see cref="OrtValue"/> reads on each run.</summary>
    public T[] Backing { get; }

    public OrtValue Value { get; }

    public void CopyFrom(ReadOnlySpan<T> source) => source.CopyTo(Backing);

    public void Dispose() => Value.Dispose();
}
