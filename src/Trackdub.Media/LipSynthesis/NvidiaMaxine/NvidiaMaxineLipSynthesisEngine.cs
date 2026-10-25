using System.Globalization;
using Trackdub.Contracts.Pipeline;
using Trackdub.Media.Process;

namespace Trackdub.Media.LipSynthesis.NvidiaMaxine;

/// <summary>
/// Video lip synthesis through NVIDIA Maxine AR SDK LipSync, run out of process. The AR SDK ships
/// its own CUDA and TensorRT DLLs, so it runs in a separate host executable rather than in
/// Trackdub's process, where other CUDA/TensorRT runtimes may already be loaded.
/// </summary>
/// <remarks>
/// The host is started once per turn and loads its models every time (about 12 s on an RTX 5070).
/// The patched clip is a full-frame video that starts at the turn start and lasts the whole turn,
/// which is what the video recomposer overlays.
/// </remarks>
public sealed class NvidiaMaxineLipSynthesisEngine : ILipSynthesisEngine
{
    public const string ProviderIdValue = "nvidia-maxine-lipsync";
    public const string ModelAliasPrefix = "nvidia-maxine-lipsync";
    public const string ModelIdValue = "nvidia/maxine/nvarlipsync";

    /// <summary>
    /// LipSync buffers 14 frames (its NumInitialFrames) before it outputs an animated frame, so
    /// shorter turns would come back unchanged or as flush frames.
    /// </summary>
    internal const int MinimumTurnFrames = 15;

    private static readonly TimeSpan DefaultPerTurnTimeout = TimeSpan.FromMinutes(30);

    private readonly Func<NvidiaMaxineLipSyncOptions> optionsProvider;
    private readonly IProcessRunner processRunner;
    private readonly FfmpegToolResolver toolResolver;
    private readonly string workRoot;

    public NvidiaMaxineLipSynthesisEngine(NvidiaMaxineLipSyncOptions options, string? ffmpegPath = null)
        : this(ConstantOptions(options), new ProcessRunner(), ffmpegPath, workRoot: null)
    {
    }

    /// <summary>
    /// Reads the configuration on every use, so changes made in Settings apply without a restart.
    /// </summary>
    public NvidiaMaxineLipSynthesisEngine(Func<NvidiaMaxineLipSyncOptions> optionsProvider, string? ffmpegPath = null)
        : this(optionsProvider, new ProcessRunner(), ffmpegPath, workRoot: null)
    {
    }

    internal NvidiaMaxineLipSynthesisEngine(
        NvidiaMaxineLipSyncOptions options,
        IProcessRunner processRunner,
        string? ffmpegPath,
        string? workRoot)
        : this(ConstantOptions(options), processRunner, ffmpegPath, workRoot)
    {
    }

    internal NvidiaMaxineLipSynthesisEngine(
        Func<NvidiaMaxineLipSyncOptions> optionsProvider,
        IProcessRunner processRunner,
        string? ffmpegPath,
        string? workRoot)
    {
        this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        toolResolver = new FfmpegToolResolver(ffmpegPath);
        this.workRoot = workRoot ?? Path.Join(Path.GetTempPath(), "trackdub-maxine-lipsync");
    }

    public bool IsAvailable => GetUnavailableReason(optionsProvider()) is null;

    /// <summary>Why the engine cannot run with the current configuration, or null when it can.</summary>
    public string? UnavailableReason => GetUnavailableReason(optionsProvider());

    public bool IsExperimental => false;

    public string ProviderId => ProviderIdValue;

    public string ModelId => ModelIdValue;

    /// <summary>
    /// Null when the host executable, the AR SDK runtime, the LipSync feature and its models are
    /// all present. This checks files only; it does not start the host.
    /// </summary>
    public static string? GetUnavailableReason(NvidiaMaxineLipSyncOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.HostExecutablePath) || !File.Exists(options.HostExecutablePath))
        {
            return $"Maxine LipSync host was not found; set {NvidiaMaxineLipSyncOptions.HostExecutableEnvironmentVariable}.";
        }

        if (string.IsNullOrWhiteSpace(options.ArSdkRoot) || !Directory.Exists(options.ArSdkRoot))
        {
            return $"Maxine AR SDK was not found; set {NvidiaMaxineLipSyncOptions.ArSdkRootEnvironmentVariable}.";
        }

        if (!File.Exists(Path.Join(options.ArSdkRoot, "bin", "nvARPose.dll")) &&
            !File.Exists(Path.Join(options.ArSdkRoot, "bin", "libnvARPose.so")))
        {
            return "Maxine AR SDK runtime (nvARPose) is missing from <ArSdkRoot>/bin.";
        }

        if (!Directory.Exists(Path.Join(options.ArSdkRoot, "features", "nvarlipsync")))
        {
            return "Maxine LipSync feature is not installed under <ArSdkRoot>/features/nvarlipsync.";
        }

        string? modelDirectory = options.ModelDirectory;
        if (modelDirectory is null || !Directory.Exists(modelDirectory) ||
            !Directory.EnumerateFiles(modelDirectory, "lipsync_fp16_*.trtpkg").Any())
        {
            return "Maxine LipSync models are missing from <ArSdkRoot>/bin/models.";
        }

        return null;
    }

    public async Task<LipSynthesisResult> SynthesizeTurnAsync(
        LipSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        NvidiaMaxineLipSyncOptions options = optionsProvider();
        string? unavailable = GetUnavailableReason(options);
        if (unavailable is not null)
        {
            return Skipped(request, unavailable);
        }

        if (request.TurnEnd <= request.TurnStart)
        {
            return Skipped(request, "Turn has no duration.");
        }

        string turnDirectory = Path.Join(workRoot, request.SegmentId.ToString("N"));
        Directory.CreateDirectory(turnDirectory);
        string turnVideoPath = Path.Join(turnDirectory, "turn.mp4");
        string turnAudioPath = Path.Join(turnDirectory, "turn-16k.wav");
        string hostOutputPath = Path.Join(turnDirectory, "lipsync.avi");
        string patchedClipPath = Path.Join(turnDirectory, "patched.mp4");

        try
        {
            string ffmpegPath = toolResolver.ResolveFfmpegPath();
            double framesPerSecond = await ProbeFrameRateAsync(request.OriginalVideoPath, cancellationToken)
                .ConfigureAwait(false);
            TurnFrameWindow window = TurnFrameWindow.Create(request.TurnStart, request.TurnEnd, framesPerSecond);
            if (window.FrameCount < MinimumTurnFrames)
            {
                return Skipped(request,
                    $"Turn is {window.FrameCount} frames; Maxine LipSync needs at least {MinimumTurnFrames} to animate the mouth.");
            }

            await RunOrThrowAsync(ffmpegPath, BuildTurnVideoArguments(request.OriginalVideoPath, window, turnVideoPath),
                "extract turn video", options: null, cancellationToken).ConfigureAwait(false);
            await RunOrThrowAsync(ffmpegPath, BuildTurnAudioArguments(request.DubbedAudioPath, window, turnAudioPath),
                "extract turn audio", options: null, cancellationToken).ConfigureAwait(false);

            NvidiaMaxineLipSyncLanguage language =
                NvidiaMaxineLipSyncLanguages.FromModelAlias(request.PreferredModelAlias) ?? options.DefaultLanguage;
            await RunOrThrowAsync(
                    options.HostExecutablePath!,
                    BuildHostArguments(options.ModelDirectory!, turnVideoPath, turnAudioPath, hostOutputPath, language),
                    "Maxine LipSync",
                    new ProcessRunOptions(
                        Timeout: options.PerTurnTimeout ?? DefaultPerTurnTimeout,
                        EnvironmentVariables: BuildHostEnvironment(options)),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!File.Exists(hostOutputPath))
            {
                return Failed(request, "Maxine LipSync finished without writing its output video.");
            }

            await RunOrThrowAsync(ffmpegPath, BuildPatchedClipArguments(hostOutputPath, window, patchedClipPath),
                "encode patched clip", options: null, cancellationToken).ConfigureAwait(false);

            return new LipSynthesisResult(
                request.SegmentId,
                LipSynthesisEngineStatus.Synthesized,
                patchedClipPath,
                SkipReason: null,
                FailureReason: null,
                ProviderId,
                ModelId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ProcessRunnerTimeoutException
                                       or FileNotFoundException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            return Failed(request, ex.Message);
        }
        finally
        {
            TryDelete(turnVideoPath);
            TryDelete(turnAudioPath);
            TryDelete(hostOutputPath);
        }
    }

    internal static IReadOnlyList<string> BuildTurnVideoArguments(
        string sourceVideoPath,
        TurnFrameWindow window,
        string outputPath) =>
    [
        "-y", "-hide_banner", "-loglevel", "error",
        "-ss", FormatSeconds(window.StartSeconds),
        "-i", Path.GetFullPath(sourceVideoPath),
        "-frames:v", window.FrameCount.ToString(CultureInfo.InvariantCulture),
        "-an",
        "-vf", $"fps={FormatSeconds(window.FramesPerSecond)}",
        "-c:v", "libx264", "-crf", "10", "-preset", "veryfast", "-pix_fmt", "yuv420p",
        Path.GetFullPath(outputPath),
    ];

    internal static IReadOnlyList<string> BuildTurnAudioArguments(
        string dubbedAudioPath,
        TurnFrameWindow window,
        string outputPath) =>
    [
        "-y", "-hide_banner", "-loglevel", "error",
        "-ss", FormatSeconds(window.StartSeconds),
        "-t", FormatSeconds(window.DurationSeconds),
        "-i", Path.GetFullPath(dubbedAudioPath),
        "-vn",
        // The host reads plain 16 kHz mono PCM WAV only (no WAVE_FORMAT_EXTENSIBLE).
        "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le",
        "-af", "apad",
        "-t", FormatSeconds(window.DurationSeconds),
        Path.GetFullPath(outputPath),
    ];

    internal static IReadOnlyList<string> BuildHostArguments(
        string modelDirectory,
        string turnVideoPath,
        string turnAudioPath,
        string outputPath,
        NvidiaMaxineLipSyncLanguage language) =>
    [
        $"--model_path={modelDirectory}",
        $"--in_video={Path.GetFullPath(turnVideoPath)}",
        $"--in_audio={Path.GetFullPath(turnAudioPath)}",
        $"--out={Path.GetFullPath(outputPath)}",
        // MJPG uses OpenCV's built-in AVI writer, so no OpenCV FFmpeg plugin DLL is needed.
        "--codec=MJPG",
        $"--language={((int)language).ToString(CultureInfo.InvariantCulture)}",
    ];

    internal static IReadOnlyList<string> BuildPatchedClipArguments(
        string hostOutputPath,
        TurnFrameWindow window,
        string outputPath) =>
    [
        "-y", "-hide_banner", "-loglevel", "error",
        "-i", Path.GetFullPath(hostOutputPath),
        // Pad by repeating the last frame in case the host returns fewer frames than the turn, then
        // cut to the exact count so the overlay never runs past the turn.
        "-vf", $"tpad=stop_mode=clone:stop=-1,fps={FormatSeconds(window.FramesPerSecond)}",
        "-frames:v", window.FrameCount.ToString(CultureInfo.InvariantCulture),
        "-an",
        "-c:v", "libx264", "-crf", "12", "-preset", "veryfast", "-pix_fmt", "yuv420p",
        Path.GetFullPath(outputPath),
    ];

    internal static IReadOnlyDictionary<string, string> BuildHostEnvironment(NvidiaMaxineLipSyncOptions options)
    {
        string sdkRoot = Path.GetFullPath(options.ArSdkRoot!);
        var searchPath = new List<string>
        {
            Path.GetDirectoryName(Path.GetFullPath(options.HostExecutablePath!))!,
            Path.Join(sdkRoot, "bin"),
        };
        string featuresRoot = Path.Join(sdkRoot, "features");
        if (Directory.Exists(featuresRoot))
        {
            searchPath.AddRange(Directory.EnumerateDirectories(featuresRoot)
                .Select(feature => Path.Join(feature, "bin"))
                .Where(Directory.Exists)
                .Order(StringComparer.OrdinalIgnoreCase));
        }

        string existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = string.Join(Path.PathSeparator, searchPath.Append(existingPath)),
            // Load the SDK from the directories above instead of Program Files.
            ["NV_AR_SDK_PATH"] = "USE_APP_PATH",
        };
    }

    private async Task<double> ProbeFrameRateAsync(string videoPath, CancellationToken cancellationToken)
    {
        string ffprobePath = toolResolver.ResolveFfprobePath();
        ProcessResult result = await processRunner.RunAsync(
            ffprobePath,
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=avg_frame_rate,r_frame_rate",
                "-of", "default=noprint_wrappers=1:nokey=1",
                Path.GetFullPath(videoPath),
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe failed on the source video: {Trim(result.StandardError)}");
        }

        return ParseFrameRate(result.StandardOutput)
            ?? throw new InvalidOperationException("Could not read the source video frame rate.");
    }

    internal static double? ParseFrameRate(string ffprobeOutput)
    {
        foreach (string line in ffprobeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
                numerator > 0 && denominator > 0)
            {
                return numerator / denominator;
            }
        }

        return null;
    }

    private async Task RunOrThrowAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string step,
        ProcessRunOptions? options,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(executablePath, arguments, cancellationToken, options)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            string detail = Trim(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
            throw new InvalidOperationException($"{step} failed with exit code {result.ExitCode}: {detail}");
        }
    }

    private static Func<NvidiaMaxineLipSyncOptions> ConstantOptions(NvidiaMaxineLipSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return () => options;
    }

    private static string FormatSeconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Trim(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[^500..];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private LipSynthesisResult Skipped(LipSynthesisRequest request, string reason) =>
        new(request.SegmentId, LipSynthesisEngineStatus.Skipped, null, reason, null, ProviderId, ModelId);

    private LipSynthesisResult Failed(LipSynthesisRequest request, string reason) =>
        new(request.SegmentId, LipSynthesisEngineStatus.Failed, null, null, reason, ProviderId, ModelId);
}

/// <summary>A speaker turn snapped to whole video frames.</summary>
internal readonly record struct TurnFrameWindow(long StartFrame, long FrameCount, double FramesPerSecond)
{
    public double StartSeconds => StartFrame / FramesPerSecond;

    public double DurationSeconds => FrameCount / FramesPerSecond;

    public static TurnFrameWindow Create(TimeSpan start, TimeSpan end, double framesPerSecond)
    {
        if (framesPerSecond <= 0 || double.IsNaN(framesPerSecond) || double.IsInfinity(framesPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Frame rate must be positive.");
        }

        long startFrame = (long)Math.Round(start.TotalSeconds * framesPerSecond, MidpointRounding.AwayFromZero);
        long endFrame = (long)Math.Round(end.TotalSeconds * framesPerSecond, MidpointRounding.AwayFromZero);
        return new TurnFrameWindow(Math.Max(0, startFrame), Math.Max(0, endFrame - Math.Max(0, startFrame)), framesPerSecond);
    }
}
