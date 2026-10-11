using System.CommandLine;

using Trackdub.Cli.Commands;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;

namespace Trackdub.Cli;

internal static class Program
{
    /// <summary>Exit code for successful execution.</summary>
    internal const int ExitSuccess = 0;

    /// <summary>Exit code for argument or validation errors.</summary>
    internal const int ExitArgumentError = 1;

    /// <summary>Exit code for pipeline execution failures.</summary>
    internal const int ExitPipelineFailure = 2;

    /// <summary>Exit code when the user or host cancels an in-flight operation (130 = 128 + SIGINT).</summary>
    internal const int ExitCancelled = 130;

    static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 && UiCommand.IsInteractiveTerminal())
            {
                return await UiCommand.RunFromMainAsync(modelDirectory: null, CancellationToken.None).ConfigureAwait(false);
            }

            var rootCommand = BuildRootCommand();
            var parseResult = rootCommand.Parse(args);

            if (IsHelpRequest(args))
            {
                using var output = new StringWriter();
                var configuration = new InvocationConfiguration
                {
                    Output = output
                };

                int exitCode = await parseResult.InvokeAsync(configuration).ConfigureAwait(false);
                await Console.Out.WriteAsync(NormalizeHelpExecutableName(output.ToString())).ConfigureAwait(false);
                return exitCode;
            }

            return await parseResult.InvokeAsync();
        }
        catch (OperationCanceledException)
        {
            return ExitCancelled;
        }
    }

    internal static RootCommand BuildRootCommand(Func<bool>? isSetupInteractive = null)
    {
        var rootCommand = new RootCommand("Trackdub CLI - headless dubbing pipeline");

        // Global options
        var modelDirectoryOption = new Option<string?>("--model-directory")
        {
            Description = "Path to the model directory containing downloaded models",
            Recursive = true
        };

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Enable verbose logging to stderr",
            Recursive = true,
            DefaultValueFactory = _ => false
        };

        var progressOption = new Option<string>("--progress")
        {
            Description = "Progress output format (json or text). Text uses live Spectre progress bars when stderr is an interactive terminal.",
            Recursive = true,
            DefaultValueFactory = _ => "text"
        };
        progressOption.AcceptOnlyFromAmong("json", "text");

        var skipPreflightOption = new Option<bool>("--skip-preflight")
        {
            Description = "Skip pre-flight model and runtime validation",
            Recursive = true,
            DefaultValueFactory = _ => false
        };

        var executionProviderOption = new Option<string>("--execution-provider")
        {
            Description = "Preferred ONNX Runtime execution provider ("
                + ExecutionProviderTokens.FormatSupportedCliTags()
                + "). Soft prefer: the planner tries this EP first, then falls through when the "
                + "engine family forbids it or smoke fails. TensorRT RTX session-init fallback applies only to supported TensorRT RTX failures. Auto lets the runtime "
                + "planner choose. On Windows, use trt-rtx for NVIDIA TensorRT RTX "
                + "(cuda is accepted as a compatibility alias for trt-rtx). "
                + "Pass --require-execution-provider to hard-pin instead of soft-prefer.",
            Recursive = true,
            DefaultValueFactory = _ => "auto"
        };
        executionProviderOption.AcceptOnlyFromAmong(ExecutionProviderTokens.CliAcceptedTokens.ToArray());

        var requireExecutionProviderOption = new Option<bool>("--require-execution-provider")
        {
            Description = "Require the --execution-provider Kind pin when the stage allow-list includes it. "
                + "Without this flag, Kind pins are soft preferences and may fall through to DirectML/CPU; "
                + "TensorRT RTX session-init fallback applies only to supported TensorRT RTX failures.",
            Recursive = true,
            DefaultValueFactory = _ => false
        };

        var preferGpuOption = new Option<bool>("--prefer-gpu")
        {
            Description = "Prefer GPU inference: vendor EP first (Windows NVIDIA → trt-rtx, AMD → migraphx, "
                + "Intel → openvino-catalog; Linux NVIDIA → cuda), then DirectML, then CPU. "
                + "Soft preference — stages may still fall through when the engine family forbids the EP or GPU is unavailable. "
                + "Explicit --execution-provider wins over this flag.",
            Recursive = true,
            DefaultValueFactory = _ => false
        };

        var requireGpuOption = new Option<bool>("--require-gpu")
        {
            Description = "Same resolution as --prefer-gpu (vendor EP first, then DirectML) but hard-requires "
                + "the preferred EP when a stage allows it. Conflict: cannot combine with --execution-provider cpu.",
            Recursive = true,
            DefaultValueFactory = _ => false
        };

        var devicePolicyOption = new Option<string>("--device-policy")
        {
            Description = "Windows ML catalog device policy (advanced, Windows-only): "
                + $"{WindowsMlExecutionDevicePolicySettings.FormatSupportedKeys()}. "
                + "Explicit keeps Trackdub's own catalog device selection; other values "
                + "delegate device choice to ONNX Runtime's SetEpSelectionPolicy. "
                + "Applies to WinML catalog EPs (migraphx, qnn, vitisai, openvino-catalog). "
                + "Ignored for cpu, directml, trt-rtx, cuda, tensorrt. "
                + "When omitted, uses the host settings.json windowsMlExecutionDevicePolicy value.",
            Recursive = true,
            DefaultValueFactory = _ => WindowsMlExecutionDevicePolicySettings.ExplicitKey
        };
        devicePolicyOption.AcceptOnlyFromAmong(
            WindowsMlExecutionDevicePolicySettings.AllPolicies.Select(
                WindowsMlExecutionDevicePolicySettings.ToKey).ToArray());

        rootCommand.Add(modelDirectoryOption);
        rootCommand.Add(verboseOption);
        rootCommand.Add(progressOption);
        rootCommand.Add(skipPreflightOption);
        rootCommand.Add(executionProviderOption);
        rootCommand.Add(requireExecutionProviderOption);
        rootCommand.Add(preferGpuOption);
        rootCommand.Add(requireGpuOption);
        rootCommand.Add(devicePolicyOption);

        // Subcommands
        rootCommand.Add(DubCommand.Create(isSetupInteractive));
        rootCommand.Add(RunCommand.Create(isSetupInteractive));
        rootCommand.Add(RunStageCommand.Create());
        rootCommand.Add(CheckCommand.Create());
        rootCommand.Add(DoctorCommand.Create());
        rootCommand.Add(ConfigCommand.Create());
        rootCommand.Add(ProjectCommand.Create());
        rootCommand.Add(ModelsCommand.Create());
        rootCommand.Add(ProvidersCommand.Create());
        rootCommand.Add(CacheCommand.Create());
        rootCommand.Add(ServeCommand.Create());
        rootCommand.Add(UiCommand.Create(isSetupInteractive));
        rootCommand.Add(CompleteCommand.Create(rootCommand));
        rootCommand.Add(CompletionCommand.Create());

        return rootCommand;
    }

    internal static string NormalizeHelpExecutableName(string helpText) =>
        helpText
            .Replace("Usage:\r\n  Trackdub.Cli", "Usage:\r\n  trackdub", StringComparison.Ordinal)
            .Replace("Usage:\n  Trackdub.Cli", "Usage:\n  trackdub", StringComparison.Ordinal);

    private static bool IsHelpRequest(IEnumerable<string> args) =>
        args.Any(static arg => arg is "--help" or "-h" or "-?");
}
