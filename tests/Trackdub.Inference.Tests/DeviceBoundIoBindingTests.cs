using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx.Benchmarking;

namespace Trackdub.Inference.Tests;

/// <summary>
/// The device-bound input path builds host OrtValues by element type. fp16 models declare
/// <see cref="Float16"/> (ORT's storage type) — a metadata-driven dispatch must reach it, or
/// fp16 models are silently excluded from device-bound benchmarking.
/// </summary>
public sealed class DeviceBoundIoBindingTests
{
    [Fact]
    public void CreateHostValue_supports_float16_dense_tensors()
    {
        var data = new Float16[] { (Float16)1.5f, (Float16)(-0.25f), (Float16)0f };
        var tensor = new DenseTensor<Float16>(data, [1, 3]);
        NamedOnnxValue input = NamedOnnxValue.CreateFromTensor("fp16_input", tensor);

        using OrtValue host = DeviceBoundIoBinding.CreateHostValue(input, typeof(Float16));

        var typeInfo = host.GetTensorTypeAndShape();
        Assert.Equal(TensorElementType.Float16, typeInfo.ElementDataType);
        Assert.Equal([1L, 3L], typeInfo.Shape);
        Assert.Equal(
            data.Select(v => (float)v).ToArray(),
            host.GetTensorDataAsSpan<Float16>().ToArray().Select(v => (float)v).ToArray());
    }

    [Fact]
    public void CreateHostValue_still_supports_system_half()
    {
        var data = new Half[] { (Half)2f, (Half)(-1f) };
        var tensor = new DenseTensor<Half>(data, [2]);
        NamedOnnxValue input = NamedOnnxValue.CreateFromTensor("half_input", tensor);

        using OrtValue host = DeviceBoundIoBinding.CreateHostValue(input, typeof(Half));

        Assert.Equal(TensorElementType.Float16, host.GetTensorTypeAndShape().ElementDataType);
        Assert.Equal([2L], host.GetTensorTypeAndShape().Shape);
    }

    [Fact]
    public void CreateHostValue_rejects_unsupported_element_type()
    {
        var tensor = new DenseTensor<byte>(new byte[] { 1, 2 }, [2]);
        NamedOnnxValue input = NamedOnnxValue.CreateFromTensor("u8_input", tensor);

        Assert.Throws<NotSupportedException>(
            () => DeviceBoundIoBinding.CreateHostValue(input, typeof(byte)));
    }
}
