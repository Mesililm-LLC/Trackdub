using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Contracts.Diagnostics;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.LipSynthesis;
using Trackdub.Inference.Onnx.LipSynthesis;

namespace Trackdub.Inference.Onnx.FaceAnalysis;

/// <summary>
/// InsightFace 2D106 facial landmark detector (MIT license).
/// Requires IFaceDetector to locate the face crop first.
/// Returns IsAvailable=false until 2d106det.onnx is present at the configured model root.
/// </summary>
public sealed class GeometryLandmarkProvider(
    IFaceDetector faceDetector,
    IVideoFrameExtractor frameExtractor,
    BenchmarkModelPathResolver modelPathResolver)
    : IFaceLandmarkProvider
{
    private const int ModelSize = 192;
    private const int LandmarkCount = 106;
    private const int OutputSize = LandmarkCount * 2; // 212 floats

    // InsightFace 2D106 mouth landmark range (indices 76–105, inclusive)
    private const int MouthStart = 76;
    private const int MouthEnd = 105;

    public bool IsAvailable
    {
        get
        {
            try { return ResolveModelPath() is not null; }
            catch { return false; }
        }
    }

    public async Task<FaceLandmarkResult> DetectLandmarksAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        string? modelPath = ResolveModelPath();
        if (modelPath is null)
            return NoLandmarks;

        FaceDetectionResult faceResult = await faceDetector.DetectPrimaryFaceAsync(
            request, cancellationToken).ConfigureAwait(false);

        if (!faceResult.FaceFound || faceResult.PrimaryFace is null)
            return NoLandmarks;

        FaceRegion face = faceResult.PrimaryFace;
        if (face.Width <= 0 || face.Height <= 0)
            return NoLandmarks;

        TimeSpan mid = request.Start + ((request.End - request.Start) / 2);
        TimeSpan sampleEnd = (request.End - mid) < TimeSpan.FromSeconds(0.5)
            ? request.End
            : mid + TimeSpan.FromSeconds(0.5);
        string tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            FrameExtractionResult extracted = await frameExtractor.ExtractTurnFramesAsync(
                request.VideoPath, mid.TotalSeconds, sampleEnd.TotalSeconds, tempDir, cancellationToken).ConfigureAwait(false);

            if (extracted.FrameCount == 0)
                return NoLandmarks;

            string framePath = Path.Join(extracted.FramesDirectory, "frame_000001.rgba");
            if (!File.Exists(framePath))
                return NoLandmarks;

            byte[] rgba = await File.ReadAllBytesAsync(framePath, cancellationToken).ConfigureAwait(false);
            float[] inputData = CropAndNormalize(rgba, extracted.FrameWidth, extracted.FrameHeight, face);

            var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };

            using var session = new InferenceSession(modelPath, opts);
            string inputName = session.InputMetadata.Keys.First();
            var inputTensor = new DenseTensor<float>(inputData, [1, 3, ModelSize, ModelSize]);

            using var outputs = session.Run(
                [NamedOnnxValue.CreateFromTensor(inputName, inputTensor)]);
            ColdStartCapture.MarkInferenceCompleted("geometry:landmarks");

            float[]? landmarks = ParseLandmarks(outputs);
            if (landmarks is null)
                return NoLandmarks;

            var pts = new (float X, float Y)[LandmarkCount];
            for (int i = 0; i < LandmarkCount; i++)
                pts[i] = (landmarks[i * 2], landmarks[(i * 2) + 1]);

            return new FaceLandmarkResult(
                LandmarksFound: true,
                IsStable: IsLandmarkSpreadValid(landmarks),
                MouthOccluded: IsMouthOccluded(landmarks),
                LandmarkCount: LandmarkCount,
                LandmarkPoints: pts);
        }
        catch
        {
            return NoLandmarks;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private string? ResolveModelPath()
    {
        BenchmarkModelResolutionResult discovery =
            modelPathResolver.Discover(LatentSyncModelPaths.LandmarkModelAlias);
        if (!string.IsNullOrWhiteSpace(discovery.Error) || discovery.Candidates.Count == 0)
            return null;
        BenchmarkModelCandidate candidate = discovery.Candidates[0];
        string modelRoot = candidate.RootDirectory
            ?? Path.GetDirectoryName(candidate.ModelPath)
            ?? string.Empty;
        if (!LatentSyncModelPaths.AreLandmarkFilesPresent(modelRoot))
            return null;
        return LatentSyncModelPaths.Landmark2D106ModelPath(modelRoot);
    }

    // Crop face region, resize to 192×192, normalize BGR (pixel−127.5)/128
    private static float[] CropAndNormalize(byte[] rgba, int imgW, int imgH, FaceRegion face)
    {
        float[] tensor = new float[3 * ModelSize * ModelSize];
        float scaleX = (float)face.Width / ModelSize;
        float scaleY = (float)face.Height / ModelSize;

        for (int y = 0; y < ModelSize; y++)
        {
            int sy = Math.Clamp((int)(face.Y + (y * scaleY)), 0, imgH - 1);
            for (int x = 0; x < ModelSize; x++)
            {
                int sx = Math.Clamp((int)(face.X + (x * scaleX)), 0, imgW - 1);
                int srcIdx = ((sy * imgW) + sx) * 4;

                tensor[(0 * ModelSize * ModelSize) + (y * ModelSize) + x] = (rgba[srcIdx + 2] - 127.5f) / 128.0f;
                tensor[(1 * ModelSize * ModelSize) + (y * ModelSize) + x] = (rgba[srcIdx + 1] - 127.5f) / 128.0f;
                tensor[(2 * ModelSize * ModelSize) + (y * ModelSize) + x] = (rgba[srcIdx] - 127.5f) / 128.0f;
            }
        }

        return tensor;
    }

    // fc1 output: [1, 212] — 106 (x,y) pairs in [−1,1] relative to 192×192 crop
    private static float[]? ParseLandmarks(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        foreach (DisposableNamedOnnxValue output in outputs)
        {
            if (output.Value is not Tensor<float> tensor) continue;
            ReadOnlySpan<int> dims = tensor.Dimensions;
            if (dims.Length == 2 && dims[0] == 1 && dims[1] == OutputSize)
            {
                var pts = new float[OutputSize];
                for (int i = 0; i < OutputSize; i++) pts[i] = tensor[0, i];
                return pts;
            }
        }

        return null;
    }

    // Mouth occlusion heuristic: if mouth landmarks (indices 76–105) span < 4% of normalized
    // face width in [-1,1] space (2.0 total), treat as occluded (e.g., mask, hand, extreme angle).
    // Landmarks are relative to 192×192 crop; spread in normalized coords correlates with mouth opening.
    private static bool IsMouthOccluded(float[] landmarks)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        for (int i = MouthStart; i <= MouthEnd; i++)
        {
            float lx = landmarks[i * 2];
            if (lx < minX) minX = lx;
            if (lx > maxX) maxX = lx;
        }

        return (maxX - minX) < 0.04f; // spread in [−1,1] space; 2.0 = full face
    }

    // Stability check: reject degenerate landmark clouds (all points collapsed to a tiny region).
    // In [-1,1] normalized crop space a real face should span at least 10% in both axes.
    private static bool IsLandmarkSpreadValid(float[] landmarks)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < LandmarkCount; i++)
        {
            float x = landmarks[i * 2];
            float y = landmarks[(i * 2) + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return (maxX - minX) > 0.10f && (maxY - minY) > 0.10f;
    }

    private static FaceLandmarkResult NoLandmarks =>
        new(LandmarksFound: false, IsStable: false, MouthOccluded: false, LandmarkCount: 0);
}
