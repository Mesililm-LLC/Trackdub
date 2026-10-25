using Trackdub.Inference.Onnx.FaceAnalysis;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class ScfrdLetterboxTests
{
    private const int InferSize = 640;
    private const float PadValue = (0f - 127.5f) / 128.0f;

    [Theory]
    [InlineData(704, 1280, 0.5f)]
    [InlineData(1920, 1080, 1f / 3f)]
    [InlineData(640, 640, 1f)]
    public void LetterboxScale_fits_the_longer_side_into_the_square_input(int width, int height, float expected)
    {
        Assert.Equal(expected, ScfrdOnnxFaceDetector.LetterboxScale(width, height), 5);
    }

    [Fact]
    public void NormalizeFrameBgr_keeps_portrait_aspect_and_pads_the_rest()
    {
        const int width = 704;
        const int height = 1280;
        byte[] rgba = new byte[width * height * 4];
        Array.Fill(rgba, (byte)255);

        float[] tensor = ScfrdOnnxFaceDetector.NormalizeFrameBgr(rgba, width, height);

        // 704x1280 scales by 0.5 to 352x640: pixels inside the scaled frame carry image data, the
        // columns to its right stay as padding instead of receiving a stretched copy of the frame.
        float white = (255f - 127.5f) / 128.0f;
        Assert.Equal(white, tensor[Index(0, 639, 351)], 5);
        Assert.Equal(PadValue, tensor[Index(0, 0, 352)], 5);
        Assert.Equal(PadValue, tensor[Index(2, 639, 639)], 5);
    }

    private static int Index(int channel, int y, int x) => (channel * InferSize * InferSize) + (y * InferSize) + x;
}
