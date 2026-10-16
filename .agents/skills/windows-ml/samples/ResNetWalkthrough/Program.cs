using System.Security.Cryptography;
using System.Text;
using System.Drawing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Windows.AI.MachineLearning;

// Adapted from Microsoft Learn. README.md maps each API pattern to its source.
// This is a single-process walkthrough, not Trackdub's production session manager.
internal static class Program
{
    private const string WebGpuName = "WebGpuExecutionProvider";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: ResNetWalkthrough <resnet50-v2-7.onnx> <image> [EpName] [CPU|GPU|NPU] [device-index] [--download] [--cache]");
            Console.WriteLine("Default: CPU, no catalog download, no compilation. Device index is within the matching EP/type list.");
            return 2;
        }

        string modelPath = Path.GetFullPath(args[0]);
        string imagePath = Path.GetFullPath(args[1]);
        if (!File.Exists(modelPath) || !File.Exists(imagePath))
            throw new FileNotFoundException("Supply an existing ONNX model and image.");

        string epName = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)
            ? args[2] : "CPUExecutionProvider";
        string typeName = args.Length > 3 && !args[3].StartsWith("--", StringComparison.Ordinal)
            ? args[3] : epName == "CPUExecutionProvider" ? "CPU" : "GPU";
        if (!Enum.TryParse(typeName, true, out OrtHardwareDeviceType deviceType) || !Enum.IsDefined(deviceType))
            throw new ArgumentException("Device type must be CPU, GPU or NPU.");
        int deviceIndex = args.Length > 4 && !args[4].StartsWith("--", StringComparison.Ordinal)
            ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 0;
        bool allowDownload = args.Contains("--download", StringComparer.Ordinal);
        bool useCache = args.Contains("--cache", StringComparer.Ordinal);

        var environment = OrtEnv.Instance();
        bool registeredWebGpu = false;
        try
        {
            bool included = epName is "CPUExecutionProvider" or "DmlExecutionProvider";
            if (!included)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                    throw new PlatformNotSupportedException("Catalog EPs require Windows 11 24H2/build 26100 or later.");
                if (!await PrepareProviderAsync(epName, allowDownload))
                    return 3;
                registeredWebGpu = string.Equals(epName, WebGpuName, StringComparison.OrdinalIgnoreCase);
            }

            var devices = environment.GetEpDevices()
                .Where(d => string.Equals(d.EpName, epName, StringComparison.OrdinalIgnoreCase)
                    && d.HardwareDevice.Type == deviceType).ToList();
            for (int i = 0; i < devices.Count; i++)
                Console.WriteLine($"Matching device {i}: {devices[i].EpName}, {devices[i].EpVendor}, {devices[i].HardwareDevice.Type}");
            if (deviceIndex < 0 || deviceIndex >= devices.Count)
                throw new InvalidOperationException("Requested EP/type/device index is unavailable. No provider substitution was made.");

            var selected = new[] { devices[deviceIndex] };
            using var options = new SessionOptions();
            // Learn's provider_specific_option is a placeholder, not a configuration key.
            options.AppendExecutionProvider(environment, selected, new Dictionary<string, string>());
            // Compatibility metadata is keyed by the EP name reported by the device,
            // which can differ in casing from the command-line selection name.
            string selectedPath = useCache && deviceType != OrtHardwareDeviceType.CPU
                ? GetValidatedModelPath(environment, options, selected, modelPath, selected[0].EpName, deviceIndex)
                : modelPath;
            using var session = new InferenceSession(selectedPath, options);
            PrintMetadata("Input", session.InputMetadata);
            PrintMetadata("Output", session.OutputMetadata);
            ValidateResNetMetadata(session);

            DenseTensor<float> tensor = Preprocess(imagePath);
            using var input = OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance, tensor.Buffer, new long[] { 1, 3, 224, 224 });
            var inputs = new Dictionary<string, OrtValue> { [session.InputMetadata.Single().Key] = input };
            using var runOptions = new RunOptions();
            using var results = session.Run(runOptions, inputs, session.OutputNames);
            float[] logits = results[0].GetTensorDataAsSpan<float>().ToArray();
            if (logits.Length != 1000 || logits.Any(v => !float.IsFinite(v)))
                throw new InvalidDataException("Expected 1000 finite ResNet logits.");
            float maximum = logits.Max();
            float[] exponentials = logits.Select(v => MathF.Exp(v - maximum)).ToArray();
            float sum = exponentials.Sum();
            foreach (var prediction in exponentials.Select((v, i) => (Index: i, Probability: v / sum))
                .OrderByDescending(v => v.Probability).Take(5))
                Console.WriteLine($"Class index {prediction.Index}: {prediction.Probability:P4}");
            Console.WriteLine("Run returned successfully. This does not establish full graph acceleration or classification accuracy.");
            return 0;
        }
        finally
        {
            // All session/options scopes end before this cleanup, including on exceptions.
            if (registeredWebGpu)
                environment.UnregisterExecutionProviderLibrary(WebGpuName);
        }
    }

    private static async Task<bool> PrepareProviderAsync(string name, bool allowDownload)
    {
        var provider = ExecutionProviderCatalog.GetDefault().FindAllProviders()
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            Console.Error.WriteLine($"{name}: not offered on this device/package combination.");
            return false;
        }
        Console.WriteLine($"{provider.Name}: {provider.ReadyState}");
        if (provider.ReadyState == ExecutionProviderReadyState.NotPresent && !allowDownload)
        {
            Console.Error.WriteLine("Not installed. Re-run with --download to allow Windows Update acquisition.");
            return false;
        }
        var result = await provider.EnsureReadyAsync();
        switch (result.Status)
        {
            case ExecutionProviderReadyResultState.Success:
                if (provider.ReadyState == ExecutionProviderReadyState.Ready && provider.TryRegister())
                    return true;
                Console.Error.WriteLine("Preparation succeeded, but registration did not succeed.");
                return false;
            case ExecutionProviderReadyResultState.InProgress:
                Console.Error.WriteLine("Installation remains in progress; retry later. No session was created.");
                return false;
            default:
                Console.Error.WriteLine($"{provider.Name}: 0x{result.ExtendedError?.HResult:X8} {result.DiagnosticText}");
                return false;
        }
    }

    private static void PrintMetadata(string kind, IReadOnlyDictionary<string, NodeMetadata> metadata)
    {
        foreach (var entry in metadata)
            Console.WriteLine($"{kind} {entry.Key}: {entry.Value.ElementType} [{string.Join(",", entry.Value.Dimensions)}]");
    }

    private static void ValidateResNetMetadata(InferenceSession session)
    {
        if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1)
            throw new NotSupportedException("This walkthrough requires one input and one output.");
        var input = session.InputMetadata.Single().Value;
        var output = session.OutputMetadata.Single().Value;
        if (input.ElementType != typeof(float) || !MatchesSingleImageShape(input.Dimensions, new[] { 3, 224, 224 })
            || output.ElementType != typeof(float) || !MatchesSingleImageShape(output.Dimensions, new[] { 1000 }))
            throw new NotSupportedException("Expected float32 input [batch,3,224,224] and float32 output [batch,1000], with batch fixed to 1 or dynamic. Use the documented ResNet export.");
    }

    private static bool MatchesSingleImageShape(int[] dimensions, int[] nonBatchDimensions) =>
        dimensions.Length == nonBatchDimensions.Length + 1
        && dimensions[0] is 1 or -1
        && dimensions.Skip(1).SequenceEqual(nonBatchDimensions);

    private static DenseTensor<float> Preprocess(string imagePath)
    {
        using var original = new Bitmap(imagePath);
        using var image = new Bitmap(224, 224);
        // Center-crop to a square, then resize. DrawImage uses source pixel coordinates.
        int edge = Math.Min(original.Width, original.Height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.DrawImage(original, new Rectangle(0, 0, 224, 224),
                (original.Width - edge) / 2, (original.Height - edge) / 2, edge, edge, GraphicsUnit.Pixel);
        }
        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] deviation = { 0.229f, 0.224f, 0.225f };
        var tensor = new DenseTensor<float>(new[] { 1, 3, 224, 224 });
        for (int y = 0; y < 224; y++)
        {
            for (int x = 0; x < 224; x++)
            {
                Color pixel = image.GetPixel(x, y);
                tensor[0, 0, y, x] = ((pixel.R / 255f) - mean[0]) / deviation[0];
                tensor[0, 1, y, x] = ((pixel.G / 255f) - mean[1]) / deviation[1];
                tensor[0, 2, y, x] = ((pixel.B / 255f) - mean[2]) / deviation[2];
            }
        }
        return tensor;
    }

    private static string GetValidatedModelPath(OrtEnv environment, SessionOptions options,
        IReadOnlyList<OrtEpDevice> devices, string source, string epName, int deviceIndex)
    {
        // Sample policy: optimal-only; source identity is independent of EP compatibility.
        // This sample accepts the single-file ResNet export, not arbitrary external-data models.
        string sourceHash;
        using (var stream = File.OpenRead(source))
            sourceHash = Convert.ToHexString(SHA256.HashData(stream));
        string identity = $"resnet-walkthrough-v1|{sourceHash}|{epName}|{deviceIndex}|empty-options";
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        string root = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsMlSkill", "ResNetWalkthrough", key);
        Directory.CreateDirectory(root);
        string indexPath = Path.Join(root, "validated.txt");
        try
        {
            if (File.Exists(indexPath) && Guid.TryParseExact(File.ReadAllText(indexPath), "N", out var generation))
            {
                string cached = Path.Join(root, generation.ToString("N"), "model.onnx");
                if (IsOptimal(environment, cached, epName, devices))
                {
                    Console.WriteLine("Reusing source-matched, EP-validated compiled model.");
                    return cached;
                }
            }
            string attempt = Guid.NewGuid().ToString("N");
            string directory = Path.Join(root, attempt);
            Directory.CreateDirectory(directory);
            string compiled = Path.Join(directory, "model.onnx");
            using (var compiler = new OrtModelCompilationOptions(options))
            {
                compiler.SetInputModelPath(source);
                compiler.SetOutputModelPath(compiled);
                compiler.CompileModel();
            }
            if (IsOptimal(environment, compiled, epName, devices))
            {
                // Preserve the entire generation directory, including external engine sidecars.
                string temporaryIndex = Path.Join(root, attempt + ".txt");
                File.WriteAllText(temporaryIndex, attempt);
                File.Move(temporaryIndex, indexPath, overwrite: true);
                Console.WriteLine("Compiled artifact validated; published cache index.");
                return compiled;
            }
        }
        catch (Exception error) when (error is OnnxRuntimeException or IOException or UnauthorizedAccessException)
        {
            // Validation errors do not justify publishing a fresh artifact during this run.
            Console.Error.WriteLine($"Cache unavailable: {error.Message}");
        }
        Console.WriteLine("Using original model; no unvalidated compiled artifact was selected.");
        return source;
    }

    private static bool IsOptimal(OrtEnv environment, string modelPath, string epName, IReadOnlyList<OrtEpDevice> devices)
    {
        if (!File.Exists(modelPath))
            return false;
        string information = environment.GetCompatibilityInfoFromModel(modelPath, epName);
        if (string.IsNullOrWhiteSpace(information))
            return false;
        var compatibility = environment.GetModelCompatibilityForEpDevices(devices, information);
        Console.WriteLine($"Compiled-model compatibility: {compatibility}");
        return compatibility == OrtCompiledModelCompatibility.EP_SUPPORTED_OPTIMAL;
    }
}
