using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Handlers;
using Trackdub.Contracts.Serving;
using Trackdub.Domain;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

internal static class ServeCommand
{
    public static Command Create()
    {
        var modelOption = new Option<string[]>("--model")
        {
            Description = "ONNX model to keep resident, as [name=]path (repeatable). The name defaults to the file name.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var warmupInputsOption = new Option<string[]>("--warmup-inputs")
        {
            Description = "Warmup inputs JSON ({\"tensors\":[...]}) as [name=]file; without a name it applies to every model. "
                + "A failing explicit warmup stops the server.",
        };
        var noWarmupOption = new Option<bool>("--no-warmup")
        {
            Description = "Skip the zero-input auto-warmup for models without --warmup-inputs.",
        };
        var trtRtxOptionOption = new Option<string[]>("--trt-rtx-option")
        {
            Description = "Extra TensorRT RTX provider option as key=value (repeatable); other providers ignore it.",
        };

        var command = new Command("serve", """
            Keep ONNX models resident on an execution provider and serve inference over stdin/stdout.

            Pays EP bootstrap, model hashing and session/engine build once, then answers each request
            from the warm, pinned session. Any provider works: --execution-provider picks it for the
            --model list (auto = best available), and a "load" request can add models on other providers.

            Protocol (JSON lines; one request per stdin line, one response per stdout line):
              {"id":"1","op":"infer","model":"enc","tensors":[{"name":"x","type":"float32","shape":[1],"data":[1.0]}]}
              {"tensors":[...]}                     op defaults to infer; model optional when one is loaded
              {"id":"2","op":"load","model":"dec","path":"dec.onnx","provider":"openvino-catalog","require":true,"warmup":true}
              {"id":"3","op":"unload","model":"dec"}
              {"id":"4","op":"health"}
            Responses carry "status" (ok, loaded, unloaded, alive, error). Errors add "reason":
            invalid-json, unknown-op, bad-inputs, unknown-model, model-not-found, load-failed,
            infer-failed, provider-unavailable. A failed run marks the model poisoned and the next
            request reopens it. Tensor types: float32, float16, int64, int32, int8, uint8, bool.
            stdout carries protocol lines only; diagnostics go to stderr. EOF on stdin stops the server.

            Examples:
              trackdub serve --model encoder=models/encoder.onnx --execution-provider trt-rtx
              trackdub serve --model model.onnx --execution-provider cpu --warmup-inputs warmup.json
            """);
        command.Options.Add(modelOption);
        command.Options.Add(warmupInputsOption);
        command.Options.Add(noWarmupOption);
        command.Options.Add(trtRtxOptionOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryParseNamedPaths(parseResult.GetValue(modelOption) ?? [], "--model", out List<ServeModelArgument> models)
                || !TryParseWarmupFiles(parseResult.GetValue(warmupInputsOption) ?? [], models, out Dictionary<string, string> warmupFiles)
                || !TryParseOptions(parseResult.GetValue(trtRtxOptionOption) ?? [], out Dictionary<string, string> trtRtxOptions))
            {
                return Program.ExitArgumentError;
            }

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            CliParseHelpers.TryResolveExecutionProviderPin(parseResult, out ExecutionProviderKind? provider, out bool require);
            using (factory)
            {
                return await ServeHandler.ServeAsync(
                    factory.GetRequiredService<IResidentInferenceHost>(),
                    models,
                    provider,
                    require,
                    trtRtxOptions,
                    warmupFiles,
                    autoWarmup: !parseResult.GetValue(noWarmupOption),
                    Console.In,
                    Console.Out,
                    Console.Error,
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }

    internal static bool TryParseNamedPaths(
        IReadOnlyList<string> values,
        string optionName,
        out List<ServeModelArgument> parsed)
    {
        parsed = [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            (string? name, string path) = SplitName(value);
            name ??= Path.GetFileNameWithoutExtension(path);
            if (!File.Exists(path))
            {
                Report($"Model file not found: {path}", optionName);
                return false;
            }

            if (!names.Add(name))
            {
                Report($"Model name '{name}' is used more than once; give each model a distinct name=path.", optionName);
                return false;
            }

            parsed.Add(new ServeModelArgument(name, path));
        }

        return true;
    }

    internal static bool TryParseWarmupFiles(
        IReadOnlyList<string> values,
        IReadOnlyList<ServeModelArgument> models,
        out Dictionary<string, string> parsed)
    {
        parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            (string? name, string path) = SplitName(value);
            if (!File.Exists(path))
            {
                Report($"Warmup inputs file not found: {path}", "--warmup-inputs");
                return false;
            }

            if (name is null)
            {
                foreach (ServeModelArgument model in models)
                {
                    parsed[model.Name] = path;
                }
            }
            else if (models.Any(model => model.Name == name))
            {
                parsed[name] = path;
            }
            else
            {
                Report($"Warmup inputs name '{name}' matches no --model.", "--warmup-inputs");
                return false;
            }
        }

        return true;
    }

    internal static bool TryParseOptions(IReadOnlyList<string> values, out Dictionary<string, string> parsed)
    {
        parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            int separator = value.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                Report($"Expected key=value, got '{value}'.", "--trt-rtx-option");
                return false;
            }

            parsed[value[..separator]] = value[(separator + 1)..];
        }

        return true;
    }

    // "name=path" only when the prefix looks like a name, so a path that contains '=' still works.
    private static (string? Name, string Path) SplitName(string value)
    {
        int separator = value.IndexOf('=', StringComparison.Ordinal);
        if (separator > 0)
        {
            string prefix = value[..separator];
            if (prefix.IndexOfAny(['/', '\\', ':', '.']) < 0)
            {
                return (prefix, value[(separator + 1)..]);
            }
        }

        return (null, value);
    }

    private static void Report(string message, string optionName) =>
        CliErrorReporter.ReportValidationError(ErrorCode.InvalidArgument, message, optionName);
}
