using Trackdub.Domain;

namespace Trackdub.Contracts.Serving;

/// <summary>Element types a resident tensor may carry, as spelled on the serve wire.</summary>
public static class ServeTensorTypes
{
    public const string Float32 = "float32";
    public const string Float16 = "float16";
    public const string Int64 = "int64";
    public const string Int32 = "int32";
    public const string Int8 = "int8";
    public const string UInt8 = "uint8";
    public const string Bool = "bool";

    public static IReadOnlyList<string> All { get; } = [Float32, Float16, Int64, Int32, Int8, UInt8, Bool];
}

/// <summary>
/// A dense tensor. <see cref="Data"/> is a flat row-major array whose element type matches
/// <see cref="DType"/>: float[], Half[], long[], int[], sbyte[], byte[] or bool[].
/// </summary>
public sealed record ServeTensor(string Name, string DType, IReadOnlyList<long> Shape, Array Data);

/// <summary>Declared input or output of a resident model; -1 marks a dynamic dimension.</summary>
public sealed record ServeTensorSpec(string Name, string DType, IReadOnlyList<long> Shape);

/// <param name="ProviderOptions">
/// Extra TensorRT RTX provider options (for example profile shapes); other providers use
/// Trackdub's own session settings and ignore them.
/// </param>
public sealed record ResidentModelSpec(
    string Name,
    string ModelPath,
    ExecutionProviderKind? PreferredProvider = null,
    bool RequireProvider = false,
    IReadOnlyDictionary<string, string>? ProviderOptions = null);

public sealed record ResidentFallbackStep(string Provider, string Reason);

public sealed record ResidentModelInfo(
    string Name,
    string ModelPath,
    string RequestedProvider,
    string SelectedProvider,
    IReadOnlyList<ResidentFallbackStep> FallbackChain,
    string? BootstrapDetail,
    IReadOnlyList<ServeTensorSpec> Inputs,
    IReadOnlyList<ServeTensorSpec> Outputs,
    bool Poisoned);

public sealed record ResidentRunResult(
    string ModelName,
    string SelectedProvider,
    IReadOnlyList<ServeTensor> Outputs,
    TimeSpan Duration);

/// <summary>Closed set of failure reasons a resident host reports.</summary>
public static class ResidentInferenceErrorReasons
{
    public const string BadInputs = "bad-inputs";
    public const string UnknownModel = "unknown-model";
    public const string ModelNotFound = "model-not-found";
    public const string LoadFailed = "load-failed";
    public const string InferFailed = "infer-failed";
    public const string ProviderUnavailable = "provider-unavailable";
}

public sealed class ResidentInferenceException(string reason, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Keeps ONNX models resident and pinned on their execution provider so repeated inference
/// pays EP bootstrap, model hashing and session/engine construction once per process.
/// </summary>
public interface IResidentInferenceHost : IDisposable
{
    /// <summary>Loads and pins a model, replacing any model already loaded under the same name.</summary>
    Task<ResidentModelInfo> LoadAsync(ResidentModelSpec spec, CancellationToken cancellationToken = default);

    Task<ResidentRunResult> RunAsync(
        string modelName,
        IReadOnlyList<ServeTensor> inputs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs once to pay first-run JIT/engine cost. Null <paramref name="inputs"/> synthesizes
    /// zero tensors from the model's declared inputs (dynamic dimensions become 1); returns
    /// null when the model has inputs that cannot be synthesized.
    /// </summary>
    Task<ResidentRunResult?> WarmAsync(
        string modelName,
        IReadOnlyList<ServeTensor>? inputs = null,
        CancellationToken cancellationToken = default);

    /// <summary>Unpins and evicts the model's pooled session; false when no such model is loaded.</summary>
    Task<bool> UnloadAsync(string modelName);

    IReadOnlyList<ResidentModelInfo> Describe();
}
