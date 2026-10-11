using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Trackdub.Contracts.Serving;
using Trackdub.Domain;

namespace Trackdub.Cli.Handlers;

internal sealed record ServeModelArgument(string Name, string Path);

/// <summary>
/// `trackdub serve`: loads models into an <see cref="IResidentInferenceHost"/>, warms them, then
/// answers one JSON request per stdin line with one compact JSON response per stdout line.
/// stdout carries protocol lines only; diagnostics go to the diagnostics writer (stderr).
/// </summary>
internal static class ServeHandler
{
    internal const string StatusOk = "ok";
    internal const string StatusLoaded = "loaded";
    internal const string StatusUnloaded = "unloaded";
    internal const string StatusAlive = "alive";
    internal const string StatusError = "error";

    internal const string ReasonInvalidJson = "invalid-json";
    internal const string ReasonUnknownOp = "unknown-op";

    private const string OpInfer = "infer";
    private const string OpLoad = "load";
    private const string OpUnload = "unload";
    private const string OpHealth = "health";

    public static async Task<int> ServeAsync(
        IResidentInferenceHost host,
        IReadOnlyList<ServeModelArgument> models,
        ExecutionProviderKind? provider,
        bool requireProvider,
        IReadOnlyDictionary<string, string> providerOptions,
        IReadOnlyDictionary<string, string> warmupInputFiles,
        bool autoWarmup,
        TextReader input,
        TextWriter output,
        TextWriter diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (ServeModelArgument model in models)
        {
            ResidentModelInfo info;
            try
            {
                info = await host.LoadAsync(
                    new ResidentModelSpec(model.Name, model.Path, provider, requireProvider, providerOptions),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ResidentInferenceException ex)
            {
                await diagnostics.WriteLineAsync($"Error: {ex.Reason}: {ex.Message}").ConfigureAwait(false);
                return Program.ExitPipelineFailure;
            }

            foreach (ResidentFallbackStep step in info.FallbackChain)
            {
                await diagnostics.WriteLineAsync(
                    $"Warning: '{info.Name}' fell back from {step.Provider}: {step.Reason}").ConfigureAwait(false);
            }

            if (!await WarmAsync(host, info, warmupInputFiles, autoWarmup, diagnostics, cancellationToken)
                    .ConfigureAwait(false))
            {
                return Program.ExitPipelineFailure;
            }
        }

        await diagnostics.WriteLineAsync("Serving. Awaiting JSON requests on stdin; EOF stops.").ConfigureAwait(false);
        return await RunLoopAsync(host, input, output, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> RunLoopAsync(
        IResidentInferenceHost host,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string response = await HandleLineAsync(host, line, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(response).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return Program.ExitSuccess;
    }

    internal static async Task<string> HandleLineAsync(
        IResidentInferenceHost host,
        string line,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return Error(null, ReasonInvalidJson, ex.Message);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(null, ReasonInvalidJson, "A request must be a JSON object.");
            }

            JsonElement? id = root.TryGetProperty("id", out JsonElement idElement) ? idElement : null;
            string op = GetString(root, "op") ?? OpInfer;
            try
            {
                return op switch
                {
                    OpInfer => await InferAsync(host, root, id, cancellationToken).ConfigureAwait(false),
                    OpLoad => await LoadAsync(host, root, id, cancellationToken).ConfigureAwait(false),
                    OpUnload => await UnloadAsync(host, root, id).ConfigureAwait(false),
                    OpHealth => Health(host, id),
                    _ => Error(id, ReasonUnknownOp, $"Unknown op '{op}'. Expected infer, load, unload or health."),
                };
            }
            catch (ResidentInferenceException ex)
            {
                return Error(id, ex.Reason, ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                string reason = op == OpLoad
                    ? ResidentInferenceErrorReasons.LoadFailed
                    : ResidentInferenceErrorReasons.InferFailed;
                return Error(id, reason, ex.Message);
            }
        }
    }

    internal static IReadOnlyList<ServeTensor> ParseTensors(JsonElement tensors)
    {
        if (tensors.ValueKind != JsonValueKind.Array)
        {
            throw BadInputs("\"tensors\" must be an array.");
        }

        var parsed = new List<ServeTensor>();
        foreach (JsonElement tensor in tensors.EnumerateArray())
        {
            string name = GetString(tensor, "name") ?? throw BadInputs("Every tensor needs a \"name\".");
            string dtype = GetString(tensor, "type") ?? GetString(tensor, "dtype")
                ?? throw BadInputs($"Tensor '{name}' needs a \"type\".");
            if (!tensor.TryGetProperty("shape", out JsonElement shapeElement) || shapeElement.ValueKind != JsonValueKind.Array)
            {
                throw BadInputs($"Tensor '{name}' needs a \"shape\" array.");
            }

            if (!tensor.TryGetProperty("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                throw BadInputs($"Tensor '{name}' needs a \"data\" array.");
            }

            long[] shape;
            try
            {
                shape = shapeElement.EnumerateArray().Select(static dimension => dimension.GetInt64()).ToArray();
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                throw BadInputs($"Tensor '{name}' shape must hold integers: {ex.Message}");
            }

            parsed.Add(new ServeTensor(name, dtype, shape, ParseData(name, dtype, dataElement)));
        }

        return parsed;
    }

    private static Array ParseData(string name, string dtype, JsonElement data)
    {
        try
        {
            return dtype switch
            {
                ServeTensorTypes.Float32 => Map(data, static item => (float)item.GetDouble()),
                ServeTensorTypes.Float16 => Map(data, static item => (Half)item.GetDouble()),
                ServeTensorTypes.Int64 => Map(data, static item => item.GetInt64()),
                ServeTensorTypes.Int32 => Map(data, static item => item.GetInt32()),
                ServeTensorTypes.Int8 => Map(data, static item => item.GetSByte()),
                ServeTensorTypes.UInt8 => Map(data, static item => item.GetByte()),
                ServeTensorTypes.Bool => Map(data, static item => item.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => item.GetInt32() != 0,
                }),
                _ => throw BadInputs(
                    $"Tensor '{name}' has unsupported type '{dtype}'. Expected one of: {string.Join(", ", ServeTensorTypes.All)}."),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            throw BadInputs($"Tensor '{name}' data is not valid {dtype}: {ex.Message}");
        }
    }

    private static T[] Map<T>(JsonElement data, Func<JsonElement, T> convert)
    {
        var values = new T[data.GetArrayLength()];
        int index = 0;
        foreach (JsonElement item in data.EnumerateArray())
        {
            values[index++] = convert(item);
        }

        return values;
    }

    private static async Task<bool> WarmAsync(
        IResidentInferenceHost host,
        ResidentModelInfo info,
        IReadOnlyDictionary<string, string> warmupInputFiles,
        bool autoWarmup,
        TextWriter diagnostics,
        CancellationToken cancellationToken)
    {
        if (warmupInputFiles.TryGetValue(info.Name, out string? warmupFile))
        {
            // An explicit warmup is a readiness check: fail fast.
            try
            {
                using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(warmupFile, cancellationToken)
                    .ConfigureAwait(false));
                if (!document.RootElement.TryGetProperty("tensors", out JsonElement tensors))
                {
                    throw BadInputs($"Warmup file '{warmupFile}' has no \"tensors\" array.");
                }

                ResidentRunResult? result = await host.WarmAsync(info.Name, ParseTensors(tensors), cancellationToken)
                    .ConfigureAwait(false);
                await diagnostics.WriteLineAsync(
                    $"Warmup: '{info.Name}' on {result?.SelectedProvider ?? info.SelectedProvider} in {FormatMilliseconds(result?.Duration)}")
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is ResidentInferenceException or JsonException or IOException)
            {
                await diagnostics.WriteLineAsync($"Error: Warmup run failed for '{info.Name}': {ex.Message}")
                    .ConfigureAwait(false);
                return false;
            }
        }

        if (!autoWarmup)
        {
            await diagnostics.WriteLineAsync($"Loaded: '{info.Name}' on {info.SelectedProvider} (no warmup).")
                .ConfigureAwait(false);
            return true;
        }

        // Zero inputs are not meaningful to every model; a failed auto-warmup still leaves the
        // session resident, so it only warns.
        try
        {
            var stopwatch = Stopwatch.StartNew();
            ResidentRunResult? result = await host.WarmAsync(info.Name, inputs: null, cancellationToken).ConfigureAwait(false);
            await diagnostics.WriteLineAsync(result is null
                    ? $"Loaded: '{info.Name}' on {info.SelectedProvider}; auto-warmup skipped (inputs cannot be synthesized)."
                    : $"Warmup: '{info.Name}' on {result.SelectedProvider} in {FormatMilliseconds(stopwatch.Elapsed)}")
                .ConfigureAwait(false);
        }
        catch (ResidentInferenceException ex)
        {
            await diagnostics.WriteLineAsync(
                $"Warning: auto-warmup for '{info.Name}' failed ({ex.Message}); the model stays loaded. "
                + "Pass --warmup-inputs for a representative warmup.").ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<string> InferAsync(
        IResidentInferenceHost host,
        JsonElement root,
        JsonElement? id,
        CancellationToken cancellationToken)
    {
        string modelName = ResolveModelName(host, GetString(root, "model"));
        if (!root.TryGetProperty("tensors", out JsonElement tensors))
        {
            throw BadInputs("An infer request needs a \"tensors\" array.");
        }

        ResidentRunResult result = await host.RunAsync(modelName, ParseTensors(tensors), cancellationToken)
            .ConfigureAwait(false);
        return Write(writer =>
        {
            WriteHeader(writer, id, StatusOk);
            writer.WriteString("model", result.ModelName);
            writer.WriteString("provider", result.SelectedProvider);
            writer.WriteNumber("durationMs", Math.Round(result.Duration.TotalMilliseconds, 3));
            writer.WriteStartArray("outputs");
            foreach (ServeTensor output in result.Outputs)
            {
                WriteTensor(writer, output);
            }

            writer.WriteEndArray();
        });
    }

    private static async Task<string> LoadAsync(
        IResidentInferenceHost host,
        JsonElement root,
        JsonElement? id,
        CancellationToken cancellationToken)
    {
        string name = GetString(root, "model") ?? throw BadInputs("A load request needs \"model\" (the name to serve it under).");
        string path = GetString(root, "path") ?? throw BadInputs("A load request needs \"path\".");
        string? providerToken = GetString(root, "provider");
        if (!ExecutionProviderTokens.TryParseCli(providerToken, out ExecutionProviderKind? provider))
        {
            throw BadInputs(
                $"Unknown provider '{providerToken}'. Expected one of: auto, {ExecutionProviderTokens.FormatSupportedCliTags()}.");
        }

        bool require = root.TryGetProperty("require", out JsonElement requireElement)
            && requireElement.ValueKind == JsonValueKind.True;
        if (require && provider is null)
        {
            throw BadInputs("\"require\" needs a non-auto \"provider\".");
        }

        Dictionary<string, string> options = new(StringComparer.Ordinal);
        if (root.TryGetProperty("trtRtxOptions", out JsonElement optionsElement) && optionsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty option in optionsElement.EnumerateObject())
            {
                options[option.Name] = option.Value.ValueKind == JsonValueKind.String
                    ? option.Value.GetString()!
                    : option.Value.GetRawText();
            }
        }

        ResidentModelInfo info = await host.LoadAsync(
            new ResidentModelSpec(name, path, provider, require, options.Count > 0 ? options : null),
            cancellationToken).ConfigureAwait(false);
        if (root.TryGetProperty("warmup", out JsonElement warmup) && warmup.ValueKind == JsonValueKind.True)
        {
            await host.WarmAsync(name, inputs: null, cancellationToken).ConfigureAwait(false);
        }

        return Write(writer =>
        {
            WriteHeader(writer, id, StatusLoaded);
            WriteModel(writer, info);
        });
    }

    private static async Task<string> UnloadAsync(IResidentInferenceHost host, JsonElement root, JsonElement? id)
    {
        string name = GetString(root, "model") ?? throw BadInputs("An unload request needs \"model\".");
        if (!await host.UnloadAsync(name).ConfigureAwait(false))
        {
            throw new ResidentInferenceException(ResidentInferenceErrorReasons.UnknownModel, $"No resident model named '{name}'.");
        }

        return Write(writer =>
        {
            WriteHeader(writer, id, StatusUnloaded);
            writer.WriteString("model", name);
        });
    }

    private static string Health(IResidentInferenceHost host, JsonElement? id) =>
        Write(writer =>
        {
            WriteHeader(writer, id, StatusAlive);
            writer.WriteStartArray("models");
            foreach (ResidentModelInfo info in host.Describe())
            {
                writer.WriteStartObject();
                WriteModel(writer, info);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });

    private static string ResolveModelName(IResidentInferenceHost host, string? requested)
    {
        if (requested is not null)
        {
            return requested;
        }

        IReadOnlyList<ResidentModelInfo> loaded = host.Describe();
        return loaded.Count == 1
            ? loaded[0].Name
            : throw BadInputs(loaded.Count == 0
                ? "No model is loaded."
                : $"Several models are loaded; name one with \"model\" ({string.Join(", ", loaded.Select(static info => info.Name))}).");
    }

    private static void WriteModel(Utf8JsonWriter writer, ResidentModelInfo info)
    {
        writer.WriteString("model", info.Name);
        writer.WriteString("path", info.ModelPath);
        writer.WriteString("requestedProvider", info.RequestedProvider);
        writer.WriteString("provider", info.SelectedProvider);
        writer.WriteBoolean("poisoned", info.Poisoned);
        if (info.BootstrapDetail is not null)
        {
            writer.WriteString("bootstrapDetail", info.BootstrapDetail);
        }

        writer.WriteStartArray("fallbackChain");
        foreach (ResidentFallbackStep step in info.FallbackChain)
        {
            writer.WriteStartObject();
            writer.WriteString("provider", step.Provider);
            writer.WriteString("reason", step.Reason);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteSpecs(writer, "inputs", info.Inputs);
        WriteSpecs(writer, "outputs", info.Outputs);
    }

    private static void WriteSpecs(Utf8JsonWriter writer, string propertyName, IReadOnlyList<ServeTensorSpec> specs)
    {
        writer.WriteStartArray(propertyName);
        foreach (ServeTensorSpec spec in specs)
        {
            writer.WriteStartObject();
            writer.WriteString("name", spec.Name);
            writer.WriteString("type", spec.DType);
            WriteShape(writer, spec.Shape);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTensor(Utf8JsonWriter writer, ServeTensor tensor)
    {
        writer.WriteStartObject();
        writer.WriteString("name", tensor.Name);
        writer.WriteString("type", tensor.DType);
        WriteShape(writer, tensor.Shape);
        writer.WriteStartArray("data");
        // Dispatch on the dtype: byte[] and sbyte[] both match either array pattern in the CLR.
        switch (tensor.DType)
        {
            case ServeTensorTypes.Float32:
                foreach (float value in (float[])tensor.Data)
                {
                    WriteFloat(writer, value);
                }

                break;
            case ServeTensorTypes.Float16:
                foreach (Half value in (Half[])tensor.Data)
                {
                    WriteFloat(writer, (float)value);
                }

                break;
            case ServeTensorTypes.Int64:
                foreach (long value in (long[])tensor.Data)
                {
                    writer.WriteNumberValue(value);
                }

                break;
            case ServeTensorTypes.Int32:
                foreach (int value in (int[])tensor.Data)
                {
                    writer.WriteNumberValue(value);
                }

                break;
            case ServeTensorTypes.Int8:
                foreach (sbyte value in (sbyte[])tensor.Data)
                {
                    writer.WriteNumberValue(value);
                }

                break;
            case ServeTensorTypes.UInt8:
                foreach (byte value in (byte[])tensor.Data)
                {
                    writer.WriteNumberValue(value);
                }

                break;
            case ServeTensorTypes.Bool:
                foreach (bool value in (bool[])tensor.Data)
                {
                    writer.WriteBooleanValue(value);
                }

                break;
            default:
                throw new NotSupportedException($"Output '{tensor.Name}' has unsupported type '{tensor.DType}'.");
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // JSON has no NaN or infinity; emit them as strings rather than failing the response.
    private static void WriteFloat(Utf8JsonWriter writer, float value)
    {
        if (float.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else
        {
            writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static void WriteShape(Utf8JsonWriter writer, IReadOnlyList<long> shape)
    {
        writer.WriteStartArray("shape");
        foreach (long dimension in shape)
        {
            writer.WriteNumberValue(dimension);
        }

        writer.WriteEndArray();
    }

    private static void WriteHeader(Utf8JsonWriter writer, JsonElement? id, string status)
    {
        writer.WritePropertyName("id");
        if (id is { } value)
        {
            value.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteString("status", status);
    }

    private static string Error(JsonElement? id, string reason, string detail) =>
        Write(writer =>
        {
            WriteHeader(writer, id, StatusError);
            writer.WriteString("reason", reason);
            writer.WriteString("detail", detail);
        });

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FormatMilliseconds(TimeSpan? duration) =>
        duration is { } value ? $"{value.TotalMilliseconds:F1} ms" : "n/a";

    private static ResidentInferenceException BadInputs(string message) =>
        new(ResidentInferenceErrorReasons.BadInputs, message);
}
