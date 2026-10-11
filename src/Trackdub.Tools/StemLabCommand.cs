using Trackdub.Media.Extraction;
using System.Diagnostics;
using System.Text.Json;

namespace Trackdub.Tools;

public static class StemLabCommand
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(1);

    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        RunAsync(args, output, error, new DefaultStemLabCommandRunner(), cancellationToken);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        IStemLabCommandRunner runner,
        CancellationToken cancellationToken)
    {
        if (!StemLabCommandOptions.TryParse(args, error, out StemLabCommandOptions options))
        {
            WriteUsage(error);
            return 1;
        }

        if (options.ShowHelp)
        {
            WriteUsage(output);
            return 0;
        }

        try
        {
            StemLabCommandResult result = await runner.RunAsync(options, cancellationToken).ConfigureAwait(false);
            WriteSummary(output, result);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.ToString());
            return 1;
        }
    }

    public static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Trackdub.Tools stem-lab");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  Trackdub.Tools stem-lab --media <path> --output <dir> --model <path> --separator-exe <path> --separator-arg <arg>...");
        writer.WriteLine();
        writer.WriteLine("Required tokens in --separator-arg values:");
        writer.WriteLine("  {input}             44.1 kHz stereo WAV extracted from the source media.");
        writer.WriteLine("  {inputFolder}       Directory containing only the extracted source WAV.");
        writer.WriteLine("  {model}             Separator model path supplied by --model.");
        writer.WriteLine("  {separatorOutput}   Directory where the separator must write stems.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --media <path>          Source video or audio file.");
        writer.WriteLine("  --output <dir>          Output directory for vocals.wav, instrumental.wav, and diagnostics.json.");
        writer.WriteLine("  --model <path>          Model file passed to the external separator command.");
        writer.WriteLine("  --config <path>         Optional config path, available as {config} in separator arguments.");
        writer.WriteLine("  --separator-exe <path>  Executable for the reference separator, for example python.exe.");
        writer.WriteLine("  --separator-arg <arg>   Repeat for each separator argument; token values are replaced after parsing.");
        writer.WriteLine("  --separator-cwd <dir>   Optional working directory for the separator process.");
        writer.WriteLine("  --ffmpeg <path>         Optional explicit ffmpeg executable path.");
        writer.WriteLine("  --keep-work             Keep the temporary source WAV and raw separator output.");
        writer.WriteLine("  --timeout-seconds <n>   Separator timeout; default is 3600.");
        writer.WriteLine("  --help                  Show this help.");
    }

    private static void WriteSummary(TextWriter writer, StemLabCommandResult result)
    {
        writer.WriteLine("StemLab complete");
        writer.WriteLine($"Source audio: {result.SourceAudioPath}");
        writer.WriteLine($"Vocals: {result.VocalsPath}");
        writer.WriteLine($"Instrumental: {result.InstrumentalPath}");
        writer.WriteLine($"Diagnostics: {result.DiagnosticsPath}");
        writer.WriteLine($"Vocals RMS: {result.Diagnostics.Vocals.RmsDbfs:F2} dBFS");
        writer.WriteLine($"Instrumental RMS: {result.Diagnostics.Instrumental.RmsDbfs:F2} dBFS");
        writer.WriteLine($"Reconstruction error: {result.Diagnostics.Reconstruction.ErrorRmsDbfs:F2} dBFS");

        if (result.Diagnostics.Warnings.Count == 0)
        {
            writer.WriteLine("Warnings: none");
            return;
        }

        writer.WriteLine("Warnings:");
        foreach (string warning in result.Diagnostics.Warnings)
        {
            writer.WriteLine($"  - {warning}");
        }
    }

    internal static TimeSpan DefaultSeparatorTimeout => DefaultTimeout;
}

public sealed record StemLabCommandOptions(
    string SourceMediaPath,
    string OutputDirectory,
    string ModelPath,
    string? ConfigPath,
    string SeparatorExecutablePath,
    IReadOnlyList<string> SeparatorArguments,
    string? SeparatorWorkingDirectory,
    string? FfmpegPath,
    bool KeepWorkDirectory,
    TimeSpan Timeout,
    bool ShowHelp)
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        TextWriter errorWriter,
        out StemLabCommandOptions options)
    {
        string? sourceMediaPath = null;
        string? outputDirectory = null;
        string? modelPath = null;
        string? configPath = null;
        string? separatorExecutablePath = null;
        var separatorArguments = new List<string>();
        string? separatorWorkingDirectory = null;
        string? ffmpegPath = null;
        bool keepWorkDirectory = false;
        TimeSpan timeout = StemLabCommand.DefaultSeparatorTimeout;
        bool showHelp = false;

        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;

                case "--media":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out sourceMediaPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--output":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out outputDirectory))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--model":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out modelPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--config":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out configPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--separator-exe":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out separatorExecutablePath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--separator-arg":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string separatorArgument))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    separatorArguments.Add(separatorArgument);
                    break;

                case "--separator-cwd":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out separatorWorkingDirectory))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--ffmpeg":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out ffmpegPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--keep-work":
                    keepWorkDirectory = true;
                    break;

                case "--timeout-seconds":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string timeoutSecondsValue) ||
                        !int.TryParse(timeoutSecondsValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int timeoutSeconds) ||
                        timeoutSeconds <= 0)
                    {
                        errorWriter.WriteLine("Argument --timeout-seconds requires a positive integer value.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    timeout = TimeSpan.FromSeconds(timeoutSeconds);
                    break;

                default:
                    errorWriter.WriteLine($"Unknown argument '{arg}'.");
                    options = DefaultWithHelp();
                    return false;
            }
        }

        if (showHelp)
        {
            options = new StemLabCommandOptions(
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                string.Empty,
                [],
                null,
                null,
                KeepWorkDirectory: false,
                StemLabCommand.DefaultSeparatorTimeout,
                ShowHelp: true);
            return true;
        }

        if (string.IsNullOrWhiteSpace(sourceMediaPath))
        {
            errorWriter.WriteLine("Missing required argument --media <path>.");
            options = DefaultWithHelp();
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            errorWriter.WriteLine("Missing required argument --output <dir>.");
            options = DefaultWithHelp();
            return false;
        }

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            errorWriter.WriteLine("Missing required argument --model <path>.");
            options = DefaultWithHelp();
            return false;
        }

        if (string.IsNullOrWhiteSpace(separatorExecutablePath))
        {
            errorWriter.WriteLine("Missing required argument --separator-exe <path>.");
            options = DefaultWithHelp();
            return false;
        }

        if (separatorArguments.Count == 0)
        {
            errorWriter.WriteLine("At least one --separator-arg <arg> value is required.");
            options = DefaultWithHelp();
            return false;
        }

        if (!ContainsToken(separatorArguments, "{input}") && !ContainsToken(separatorArguments, "{inputFolder}"))
        {
            errorWriter.WriteLine("Separator arguments must include either the {input} or {inputFolder} token.");
            options = DefaultWithHelp();
            return false;
        }

        if (!ContainsToken(separatorArguments, "{model}"))
        {
            errorWriter.WriteLine("Separator arguments must include the {model} token.");
            options = DefaultWithHelp();
            return false;
        }

        if (!ContainsToken(separatorArguments, "{separatorOutput}"))
        {
            errorWriter.WriteLine("Separator arguments must include the {separatorOutput} token.");
            options = DefaultWithHelp();
            return false;
        }

        if (ContainsToken(separatorArguments, "{config}") && string.IsNullOrWhiteSpace(configPath))
        {
            errorWriter.WriteLine("Separator arguments include {config}, but --config <path> was not provided.");
            options = DefaultWithHelp();
            return false;
        }

        options = new StemLabCommandOptions(
            Path.GetFullPath(sourceMediaPath),
            Path.GetFullPath(outputDirectory),
            Path.GetFullPath(modelPath),
            NormalizeOptionalPath(configPath),
            NormalizeExecutablePath(separatorExecutablePath),
            separatorArguments.ToArray(),
            NormalizeOptionalPath(separatorWorkingDirectory),
            NormalizeOptionalExecutablePath(ffmpegPath),
            keepWorkDirectory,
            timeout,
            ShowHelp: false);
        return true;
    }

    private static StemLabCommandOptions DefaultWithHelp() =>
        new(
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            string.Empty,
            [],
            null,
            null,
            KeepWorkDirectory: false,
            StemLabCommand.DefaultSeparatorTimeout,
            ShowHelp: true);

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        TextWriter errorWriter,
        out string value)
    {
        if (index + 1 >= args.Count)
        {
            errorWriter.WriteLine($"Missing value for {optionName}.");
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool ContainsToken(IEnumerable<string> arguments, string token) =>
        arguments.Any(argument => argument.Contains(token, StringComparison.Ordinal));

    private static string? NormalizeOptionalPath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);

    private static string NormalizeExecutablePath(string value)
    {
        string trimmed = value.Trim();
        return HasPathSeparator(trimmed) || Path.IsPathFullyQualified(trimmed)
            ? Path.GetFullPath(trimmed)
            : trimmed;
    }

    private static string? NormalizeOptionalExecutablePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeExecutablePath(value);

    private static bool HasPathSeparator(string value) =>
        value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        value.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
}

public interface IStemLabCommandRunner
{
    Task<StemLabCommandResult> RunAsync(StemLabCommandOptions options, CancellationToken cancellationToken);
}

public sealed class DefaultStemLabCommandRunner : IStemLabCommandRunner
{
    private static readonly JsonSerializerOptions DiagnosticsJsonOptions = new() { WriteIndented = true };
    private readonly StemLabProcessRunner processRunner;

    public DefaultStemLabCommandRunner()
        : this(new StemLabProcessRunner())
    {
    }

    internal DefaultStemLabCommandRunner(StemLabProcessRunner processRunner)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<StemLabCommandResult> RunAsync(StemLabCommandOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(options.SourceMediaPath))
        {
            throw new FileNotFoundException("Source media file was not found.", options.SourceMediaPath);
        }

        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException("BS-RoFormer model file was not found.", options.ModelPath);
        }

        if (options.ConfigPath is not null && !File.Exists(options.ConfigPath))
        {
            throw new FileNotFoundException("BS-RoFormer config file was not found.", options.ConfigPath);
        }

        Directory.CreateDirectory(options.OutputDirectory);
        string workDirectory = Path.Join(options.OutputDirectory, "_stemlab_work");
        string separatorInputDirectory = Path.Join(workDirectory, "separator-input");
        string separatorOutputDirectory = Path.Join(workDirectory, "separator-output");
        RecreateDirectory(workDirectory);
        Directory.CreateDirectory(separatorInputDirectory);
        Directory.CreateDirectory(separatorOutputDirectory);

        string sourceAudioPath = Path.Join(options.OutputDirectory, "stem-source.wav");
        string separatorSourceAudioPath = Path.Join(separatorInputDirectory, "stem-source.wav");
        string vocalsPath = Path.Join(options.OutputDirectory, "vocals.wav");
        string instrumentalPath = Path.Join(options.OutputDirectory, "instrumental.wav");
        string diagnosticsPath = Path.Join(options.OutputDirectory, "diagnostics.json");

        var extractionService = new FfmpegAudioExtractionService(options.FfmpegPath);
        await extractionService.ExtractStemSeparationAudioAsync(
            options.SourceMediaPath,
            sourceAudioPath,
            cancellationToken).ConfigureAwait(false);
        File.Copy(sourceAudioPath, separatorSourceAudioPath, overwrite: true);

        IReadOnlyList<string> separatorArguments = ResolveSeparatorArguments(
            options,
            sourceAudioPath,
            separatorInputDirectory,
            separatorOutputDirectory);

        StemLabProcessResult separatorResult = await processRunner.RunAsync(
            options.SeparatorExecutablePath,
            separatorArguments,
            options.SeparatorWorkingDirectory,
            options.Timeout,
            cancellationToken).ConfigureAwait(false);

        if (separatorResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"BS-RoFormer separator command failed with exit code {separatorResult.ExitCode}: {separatorResult.StandardError}".Trim());
        }

        string rawVocalsPath = FindStemFile(separatorOutputDirectory, StemLabStemKind.Vocals, excludedPath: null);
        string rawInstrumentalPath = FindStemFile(separatorOutputDirectory, StemLabStemKind.Instrumental, rawVocalsPath);

        await extractionService.ExtractStemSeparationAudioAsync(rawVocalsPath, vocalsPath, cancellationToken).ConfigureAwait(false);
        await extractionService.ExtractStemSeparationAudioAsync(rawInstrumentalPath, instrumentalPath, cancellationToken).ConfigureAwait(false);

        StemLabDiagnostics diagnostics = await StemLabDiagnosticsBuilder.BuildAsync(
            sourceAudioPath,
            vocalsPath,
            instrumentalPath,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            diagnosticsPath,
            JsonSerializer.Serialize(diagnostics, DiagnosticsJsonOptions),
            cancellationToken).ConfigureAwait(false);

        if (!options.KeepWorkDirectory)
        {
            Directory.Delete(workDirectory, recursive: true);
        }

        return new StemLabCommandResult(
            sourceAudioPath,
            vocalsPath,
            instrumentalPath,
            diagnosticsPath,
            diagnostics);
    }

    private static IReadOnlyList<string> ResolveSeparatorArguments(
        StemLabCommandOptions options,
        string sourceAudioPath,
        string separatorInputDirectory,
        string separatorOutputDirectory)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{input}"] = sourceAudioPath,
            ["{sourceAudio}"] = sourceAudioPath,
            ["{inputFolder}"] = separatorInputDirectory,
            ["{inputDirectory}"] = separatorInputDirectory,
            ["{model}"] = options.ModelPath,
            ["{output}"] = separatorOutputDirectory,
            ["{separatorOutput}"] = separatorOutputDirectory
        };

        if (options.ConfigPath is not null)
        {
            replacements["{config}"] = options.ConfigPath;
        }

        return options.SeparatorArguments
            .Select(argument =>
            {
                string resolved = argument;
                foreach (KeyValuePair<string, string> replacement in replacements)
                {
                    resolved = resolved.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
                }

                return resolved;
            })
            .ToArray();
    }

    private static void RecreateDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Directory.CreateDirectory(directoryPath);
    }

    private static string FindStemFile(
        string directoryPath,
        StemLabStemKind stemKind,
        string? excludedPath)
    {
        string[] candidates = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
            .Where(IsSupportedAudioFile)
            .Where(path => excludedPath is null || !Path.GetFullPath(path).Equals(Path.GetFullPath(excludedPath), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException($"Separator output did not contain a supported {stemKind.ToString().ToLowerInvariant()} audio file.");
        }

        string? bestPath = candidates
            .Select(path => new { Path = path, Score = ScoreStemCandidate(path, stemKind) })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(candidate => candidate.Score > 0)
            ?.Path;

        if (bestPath is not null)
        {
            return bestPath;
        }

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        throw new InvalidOperationException(
            $"Could not identify a {stemKind.ToString().ToLowerInvariant()} stem in separator output. Files: {string.Join(", ", candidates.Select(Path.GetFileName))}");
    }

    private static bool IsSupportedAudioFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreStemCandidate(string path, StemLabStemKind stemKind)
    {
        string fileName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return stemKind switch
        {
            StemLabStemKind.Vocals => ScoreVocals(fileName),
            StemLabStemKind.Instrumental => ScoreInstrumental(fileName),
            _ => 0
        };
    }

    private static int ScoreVocals(string fileName)
    {
        int score = 0;
        if (fileName.Contains("vocals", StringComparison.Ordinal))
        {
            score += 100;
        }
        else if (fileName.Contains("vocal", StringComparison.Ordinal))
        {
            score += 80;
        }
        else if (fileName.Contains("voice", StringComparison.Ordinal))
        {
            score += 60;
        }

        if (fileName.Contains("instrumental", StringComparison.Ordinal) ||
            fileName.Contains("accompaniment", StringComparison.Ordinal) ||
            fileName.Contains("no_vocals", StringComparison.Ordinal) ||
            fileName.Contains("novocals", StringComparison.Ordinal))
        {
            score -= 100;
        }

        return score;
    }

    private static int ScoreInstrumental(string fileName)
    {
        int score = 0;
        if (fileName.Contains("instrumental", StringComparison.Ordinal))
        {
            score += 100;
        }
        else if (fileName.Contains("accompaniment", StringComparison.Ordinal))
        {
            score += 90;
        }
        else if (fileName.Contains("no_vocals", StringComparison.Ordinal) ||
                 fileName.Contains("novocals", StringComparison.Ordinal))
        {
            score += 80;
        }
        else if (fileName.Contains("karaoke", StringComparison.Ordinal))
        {
            score += 60;
        }

        if ((fileName.Contains("vocals", StringComparison.Ordinal) ||
             fileName.Contains("vocal", StringComparison.Ordinal) ||
             fileName.Contains("voice", StringComparison.Ordinal)) &&
            !fileName.Contains("no_vocals", StringComparison.Ordinal) &&
            !fileName.Contains("novocals", StringComparison.Ordinal))
        {
            score -= 100;
        }

        return score;
    }

    private enum StemLabStemKind
    {
        Vocals,
        Instrumental
    }
}

public sealed record StemLabCommandResult(
    string SourceAudioPath,
    string VocalsPath,
    string InstrumentalPath,
    string DiagnosticsPath,
    StemLabDiagnostics Diagnostics);

public sealed record StemLabDiagnostics(
    StemLabAudioMetrics Source,
    StemLabAudioMetrics Vocals,
    StemLabAudioMetrics Instrumental,
    StemLabReconstructionMetrics Reconstruction,
    IReadOnlyList<string> Warnings);

public sealed record StemLabAudioMetrics(
    string Label,
    int SampleRate,
    int ChannelCount,
    long SampleFrames,
    double DurationSeconds,
    double PeakDbfs,
    double RmsDbfs,
    double ClippedSamplePercent,
    double HighFrequencyEnergyRatio);

public sealed record StemLabReconstructionMetrics(
    double ComparedDurationSeconds,
    double ErrorRmsDbfs,
    double ErrorToSourceRmsRatio);

internal sealed class StemLabProcessRunner
{
    public async Task<StemLabProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellationTokenSource = new CancellationTokenSource(timeout);
        using CancellationTokenSource linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellationTokenSource.Token);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start separator executable '{executablePath}'.");
            }

            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(linkedCancellationTokenSource.Token);
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(linkedCancellationTokenSource.Token);
            await process.WaitForExitAsync(linkedCancellationTokenSource.Token).ConfigureAwait(false);
            string standardOutput = await standardOutputTask.ConfigureAwait(false);
            string standardError = await standardErrorTask.ConfigureAwait(false);
            return new StemLabProcessResult(process.ExitCode, standardOutput, standardError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Separator command exceeded the timeout of {timeout.TotalSeconds:F0} seconds.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Failed to start separator executable '{executablePath}': {ex.Message}", ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

internal sealed record StemLabProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal static class StemLabDiagnosticsBuilder
{
    public static async Task<StemLabDiagnostics> BuildAsync(
        string sourceAudioPath,
        string vocalsPath,
        string instrumentalPath,
        CancellationToken cancellationToken)
    {
        StemLabAudioMetrics source = await StemLabWaveAnalyzer.AnalyzeAsync("source", sourceAudioPath, cancellationToken).ConfigureAwait(false);
        StemLabAudioMetrics vocals = await StemLabWaveAnalyzer.AnalyzeAsync("vocals", vocalsPath, cancellationToken).ConfigureAwait(false);
        StemLabAudioMetrics instrumental = await StemLabWaveAnalyzer.AnalyzeAsync("instrumental", instrumentalPath, cancellationToken).ConfigureAwait(false);
        StemLabReconstructionMetrics reconstruction = await StemLabWaveAnalyzer.CompareReconstructionAsync(
            sourceAudioPath,
            vocalsPath,
            instrumentalPath,
            cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        AddAudioWarnings(warnings, vocals);
        AddAudioWarnings(warnings, instrumental);

        if (reconstruction.ErrorToSourceRmsRatio > 0.25)
        {
            warnings.Add($"Stem sum does not reconstruct the source cleanly; error/source RMS ratio is {reconstruction.ErrorToSourceRmsRatio:F3}.");
        }

        return new StemLabDiagnostics(source, vocals, instrumental, reconstruction, warnings);
    }

    private static void AddAudioWarnings(ICollection<string> warnings, StemLabAudioMetrics metrics)
    {
        if (metrics.RmsDbfs < -80.0)
        {
            warnings.Add($"{metrics.Label} stem is effectively silent ({metrics.RmsDbfs:F2} dBFS RMS).");
        }

        if (metrics.ClippedSamplePercent > 0.1)
        {
            warnings.Add($"{metrics.Label} stem has {metrics.ClippedSamplePercent:F3}% clipped samples.");
        }

        if (metrics.HighFrequencyEnergyRatio > 0.35 && metrics.RmsDbfs > -60.0)
        {
            warnings.Add($"{metrics.Label} stem has suspicious high-frequency energy ({metrics.HighFrequencyEnergyRatio:F3}).");
        }
    }
}

internal static class StemLabWaveAnalyzer
{
    private const int PcmFormat = 1;
    private const int BitsPerSample = 16;

    public static async Task<StemLabAudioMetrics> AnalyzeAsync(
        string label,
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        WavePcm16Header header = ReadHeader(stream, path);
        stream.Position = header.DataOffset;

        byte[] buffer = new byte[64 * 1024];
        long remainingBytes = header.DataByteLength;
        long sampleCount = 0;
        long clippedSamples = 0;
        double peak = 0.0;
        double sumSquares = 0.0;
        double sumDifferenceSquares = 0.0;
        double[] previousByChannel = new double[header.ChannelCount];
        bool[] hasPreviousByChannel = new bool[header.ChannelCount];

        while (remainingBytes > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesToRead = (int)Math.Min(buffer.Length, remainingBytes);
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            remainingBytes -= bytesRead;
            int sampleBytes = bytesRead - (bytesRead % sizeof(short));
            for (int offset = 0; offset < sampleBytes; offset += sizeof(short))
            {
                short pcmSample = ReadInt16LittleEndian(buffer, offset);
                double sample = pcmSample / 32768.0;
                double abs = Math.Abs(sample);
                int channel = (int)(sampleCount % header.ChannelCount);

                peak = Math.Max(peak, abs);
                sumSquares += sample * sample;
                if (Math.Abs(pcmSample) >= 32767)
                {
                    clippedSamples++;
                }

                if (hasPreviousByChannel[channel])
                {
                    double difference = sample - previousByChannel[channel];
                    sumDifferenceSquares += difference * difference;
                }

                previousByChannel[channel] = sample;
                hasPreviousByChannel[channel] = true;
                sampleCount++;
            }
        }

        double rms = sampleCount == 0 ? 0.0 : Math.Sqrt(sumSquares / sampleCount);
        double clippedPercent = sampleCount == 0 ? 0.0 : clippedSamples * 100.0 / sampleCount;
        double highFrequencyRatio = sumSquares <= 0.0
            ? 0.0
            : Math.Clamp(sumDifferenceSquares / (4.0 * sumSquares), 0.0, 1.0);

        return new StemLabAudioMetrics(
            label,
            header.SampleRate,
            header.ChannelCount,
            header.SampleFrames,
            header.DurationSeconds,
            ToDbfs(peak),
            ToDbfs(rms),
            clippedPercent,
            highFrequencyRatio);
    }

    public static async Task<StemLabReconstructionMetrics> CompareReconstructionAsync(
        string sourceAudioPath,
        string vocalsPath,
        string instrumentalPath,
        CancellationToken cancellationToken)
    {
        await using FileStream sourceStream = File.OpenRead(sourceAudioPath);
        await using FileStream vocalsStream = File.OpenRead(vocalsPath);
        await using FileStream instrumentalStream = File.OpenRead(instrumentalPath);

        WavePcm16Header sourceHeader = ReadHeader(sourceStream, sourceAudioPath);
        WavePcm16Header vocalsHeader = ReadHeader(vocalsStream, vocalsPath);
        WavePcm16Header instrumentalHeader = ReadHeader(instrumentalStream, instrumentalPath);

        EnsureComparable(sourceHeader, vocalsHeader, sourceAudioPath, vocalsPath);
        EnsureComparable(sourceHeader, instrumentalHeader, sourceAudioPath, instrumentalPath);

        sourceStream.Position = sourceHeader.DataOffset;
        vocalsStream.Position = vocalsHeader.DataOffset;
        instrumentalStream.Position = instrumentalHeader.DataOffset;

        byte[] sourceBuffer = new byte[64 * 1024];
        byte[] vocalsBuffer = new byte[64 * 1024];
        byte[] instrumentalBuffer = new byte[64 * 1024];
        long remainingBytes = Math.Min(sourceHeader.DataByteLength, Math.Min(vocalsHeader.DataByteLength, instrumentalHeader.DataByteLength));
        long comparedSamples = 0;
        double sumSourceSquares = 0.0;
        double sumErrorSquares = 0.0;

        while (remainingBytes > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesToRead = (int)Math.Min(sourceBuffer.Length, remainingBytes);
            int sourceRead = await sourceStream.ReadAsync(sourceBuffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
            int vocalsRead = await vocalsStream.ReadAsync(vocalsBuffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
            int instrumentalRead = await instrumentalStream.ReadAsync(instrumentalBuffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
            int bytesRead = Math.Min(sourceRead, Math.Min(vocalsRead, instrumentalRead));
            if (bytesRead == 0)
            {
                break;
            }

            remainingBytes -= bytesRead;
            int sampleBytes = bytesRead - (bytesRead % sizeof(short));
            for (int offset = 0; offset < sampleBytes; offset += sizeof(short))
            {
                double source = ReadInt16LittleEndian(sourceBuffer, offset) / 32768.0;
                double vocals = ReadInt16LittleEndian(vocalsBuffer, offset) / 32768.0;
                double instrumental = ReadInt16LittleEndian(instrumentalBuffer, offset) / 32768.0;
                double error = source - (vocals + instrumental);
                sumSourceSquares += source * source;
                sumErrorSquares += error * error;
                comparedSamples++;
            }
        }

        double errorRms = comparedSamples == 0 ? 0.0 : Math.Sqrt(sumErrorSquares / comparedSamples);
        double sourceRms = comparedSamples == 0 ? 0.0 : Math.Sqrt(sumSourceSquares / comparedSamples);
        double comparedFrames = sourceHeader.ChannelCount == 0 ? 0.0 : comparedSamples / (double)sourceHeader.ChannelCount;

        return new StemLabReconstructionMetrics(
            comparedFrames / sourceHeader.SampleRate,
            ToDbfs(errorRms),
            sourceRms <= 0.0 ? 0.0 : errorRms / sourceRms);
    }

    internal static WavePcm16Header ReadHeader(Stream stream, string path)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        string riff = new(reader.ReadChars(4));
        _ = reader.ReadUInt32();
        string wave = new(reader.ReadChars(4));
        bool isRf64 = riff.Equals("RF64", StringComparison.Ordinal);
        if ((!isRf64 && !riff.Equals("RIFF", StringComparison.Ordinal)) || !wave.Equals("WAVE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"File '{path}' is not a RIFF/WAVE file.");
        }

        int? channelCount = null;
        int? sampleRate = null;
        short? bitsPerSample = null;
        short? audioFormat = null;
        long? dataOffset = null;
        long? dataByteLength = null;
        ulong? ds64DataLength = null;

        while (stream.Position + 8 <= stream.Length)
        {
            string chunkId = new(reader.ReadChars(4));
            uint chunkSize = reader.ReadUInt32();
            long chunkDataOffset = stream.Position;
            long chunkLength = chunkSize;

            if (isRf64 && chunkId.Equals("ds64", StringComparison.Ordinal) && chunkSize >= 16)
            {
                _ = reader.ReadUInt64();
                ds64DataLength = reader.ReadUInt64();
            }
            else if (chunkId.Equals("fmt ", StringComparison.Ordinal))
            {
                audioFormat = reader.ReadInt16();
                channelCount = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadInt32();
                _ = reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
            }
            else if (chunkId.Equals("data", StringComparison.Ordinal))
            {
                // RF64 writers (ffmpeg -rf64 auto) put 0xFFFFFFFF here and the real size in ds64.
                if (isRf64 && chunkSize == uint.MaxValue)
                {
                    if (ds64DataLength is not ulong length || length > long.MaxValue)
                    {
                        throw new InvalidOperationException($"File '{path}' is RF64 but has no valid ds64 data size.");
                    }

                    chunkLength = (long)length;
                }

                dataOffset = chunkDataOffset;
                dataByteLength = chunkLength;
            }

            stream.Position = chunkDataOffset + chunkLength + (chunkLength % 2);
            if (channelCount.HasValue && sampleRate.HasValue && bitsPerSample.HasValue && audioFormat.HasValue && dataOffset.HasValue && dataByteLength.HasValue)
            {
                break;
            }
        }

        if (audioFormat is not PcmFormat || bitsPerSample is not BitsPerSample || channelCount is null || sampleRate is null || dataOffset is null || dataByteLength is null)
        {
            throw new InvalidOperationException($"File '{path}' must be PCM16 WAV audio.");
        }

        long sampleCount = dataByteLength.Value / sizeof(short);
        long sampleFrames = channelCount.Value == 0 ? 0 : sampleCount / channelCount.Value;
        return new WavePcm16Header(
            sampleRate.Value,
            channelCount.Value,
            sampleFrames,
            dataOffset.Value,
            dataByteLength.Value);
    }

    private static void EnsureComparable(
        WavePcm16Header expected,
        WavePcm16Header actual,
        string expectedPath,
        string actualPath)
    {
        if (expected.SampleRate != actual.SampleRate || expected.ChannelCount != actual.ChannelCount)
        {
            throw new InvalidOperationException(
                $"Cannot compare reconstruction for '{expectedPath}' and '{actualPath}' because their WAV formats differ.");
        }
    }

    private static short ReadInt16LittleEndian(byte[] buffer, int offset) =>
        unchecked((short)(buffer[offset] | (buffer[offset + 1] << 8)));

    private static double ToDbfs(double value) =>
        value <= 0.0 ? -120.0 : 20.0 * Math.Log10(value);

    internal sealed record WavePcm16Header(
        int SampleRate,
        int ChannelCount,
        long SampleFrames,
        long DataOffset,
        long DataByteLength)
    {
        public double DurationSeconds => SampleRate <= 0 ? 0.0 : SampleFrames / (double)SampleRate;
    }
}
