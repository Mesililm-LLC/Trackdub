using Trackdub.Contracts.Pipeline;
using Trackdub.Media.LipSynthesis.NvidiaMaxine;
using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

public sealed class NvidiaMaxineLipSynthesisEngineTests : IDisposable
{
    private readonly string root = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", "maxine-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TurnFrameWindow_snaps_turn_edges_to_whole_frames()
    {
        TurnFrameWindow window = TurnFrameWindow.Create(TimeSpan.FromSeconds(1.111706), TimeSpan.FromSeconds(3.8115634), 30);

        Assert.Equal(33, window.StartFrame);
        Assert.Equal(81, window.FrameCount);
        Assert.Equal(1.1, window.StartSeconds, 6);
        Assert.Equal(2.7, window.DurationSeconds, 6);
    }

    [Fact]
    public void TurnFrameWindow_reports_zero_frames_for_a_sub_frame_turn()
    {
        TurnFrameWindow window = TurnFrameWindow.Create(TimeSpan.FromSeconds(1.000), TimeSpan.FromSeconds(1.010), 30);

        Assert.Equal(0, window.FrameCount);
    }

    [Theory]
    [InlineData("30/1\n30/1\n", 30.0)]
    [InlineData("30000/1001\n", 29.97003)]
    [InlineData("0/0\n25/1\n", 25.0)]
    public void ParseFrameRate_reads_the_first_valid_ffprobe_rate(string output, double expected)
    {
        Assert.Equal(expected, NvidiaMaxineLipSynthesisEngine.ParseFrameRate(output)!.Value, 4);
    }

    [Fact]
    public void ParseFrameRate_returns_null_when_no_rate_is_usable()
    {
        Assert.Null(NvidiaMaxineLipSynthesisEngine.ParseFrameRate("0/0\nN/A\n"));
    }

    [Theory]
    [InlineData("es", NvidiaMaxineLipSyncLanguage.Spanish)]
    [InlineData("German", NvidiaMaxineLipSyncLanguage.German)]
    [InlineData("2", NvidiaMaxineLipSyncLanguage.French)]
    [InlineData("generic", NvidiaMaxineLipSyncLanguage.Generic)]
    public void Languages_parse_codes_names_and_flag_values(string value, NvidiaMaxineLipSyncLanguage expected)
    {
        Assert.Equal(expected, NvidiaMaxineLipSyncLanguages.Parse(value));
    }

    [Theory]
    [InlineData("nvidia-maxine-lipsync-es", NvidiaMaxineLipSyncLanguage.Spanish)]
    [InlineData("nvidia-maxine-lipsync:de", NvidiaMaxineLipSyncLanguage.German)]
    [InlineData("nvidia-maxine-lipsync", null)]
    [InlineData("latentsync-1.6", null)]
    [InlineData(null, null)]
    public void Languages_read_a_language_suffix_from_the_model_alias(string? alias, NvidiaMaxineLipSyncLanguage? expected)
    {
        Assert.Equal(expected, NvidiaMaxineLipSyncLanguages.FromModelAlias(alias));
    }

    [Fact]
    public void Engine_is_unavailable_until_host_runtime_feature_and_models_exist()
    {
        string host = Path.Join(root, "host", "LipSyncApp.exe");
        string sdk = Path.Join(root, "ARSDK");
        var options = new NvidiaMaxineLipSyncOptions(host, sdk);

        Assert.Contains(NvidiaMaxineLipSyncOptions.HostExecutableEnvironmentVariable,
            NvidiaMaxineLipSynthesisEngine.GetUnavailableReason(options));

        WriteFile(host);
        Assert.Contains(NvidiaMaxineLipSyncOptions.ArSdkRootEnvironmentVariable,
            NvidiaMaxineLipSynthesisEngine.GetUnavailableReason(options));

        Directory.CreateDirectory(sdk);
        Assert.Contains("nvARPose", NvidiaMaxineLipSynthesisEngine.GetUnavailableReason(options));

        WriteFile(Path.Join(sdk, "bin", "nvARPose.dll"));
        Assert.Contains("feature", NvidiaMaxineLipSynthesisEngine.GetUnavailableReason(options));

        Directory.CreateDirectory(Path.Join(sdk, "features", "nvarlipsync", "bin"));
        Assert.Contains("models", NvidiaMaxineLipSynthesisEngine.GetUnavailableReason(options));

        WriteFile(Path.Join(sdk, "bin", "models", "lipsync_fp16_100.engine.trtpkg"));
        Assert.Null(NvidiaMaxineLipSynthesisEngine.GetUnavailableReason(options));
        Assert.True(new NvidiaMaxineLipSynthesisEngine(options).IsAvailable);
    }

    [Fact]
    public void Host_arguments_select_the_model_language_and_the_bundled_mjpg_writer()
    {
        IReadOnlyList<string> args = NvidiaMaxineLipSynthesisEngine.BuildHostArguments(
            "D:/sdk/bin/models", "turn.mp4", "turn.wav", "out.avi", NvidiaMaxineLipSyncLanguage.Spanish);

        Assert.Contains("--model_path=D:/sdk/bin/models", args);
        Assert.Contains("--codec=MJPG", args);
        Assert.Contains("--language=3", args);
        Assert.Contains(args, arg => arg.StartsWith("--in_audio=", StringComparison.Ordinal) && arg.EndsWith("turn.wav", StringComparison.Ordinal));
    }

    [Fact]
    public void Host_environment_puts_the_sdk_and_feature_folders_first_and_uses_the_app_path()
    {
        string host = Path.Join(root, "host", "LipSyncApp.exe");
        string sdk = Path.Join(root, "ARSDK");
        Directory.CreateDirectory(Path.Join(sdk, "features", "nvarlipsync", "bin"));
        Directory.CreateDirectory(Path.Join(sdk, "features", "nvarfaceboxdetection", "bin"));

        IReadOnlyDictionary<string, string> environment =
            NvidiaMaxineLipSynthesisEngine.BuildHostEnvironment(new NvidiaMaxineLipSyncOptions(host, sdk));

        Assert.Equal("USE_APP_PATH", environment["NV_AR_SDK_PATH"]);
        string[] path = environment["PATH"].Split(Path.PathSeparator);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(host)), path[0]);
        Assert.Equal(Path.Join(Path.GetFullPath(sdk), "bin"), path[1]);
        Assert.Contains(Path.Join(Path.GetFullPath(sdk), "features", "nvarlipsync", "bin"), path);
    }

    [Fact]
    public void Turn_extraction_cuts_the_snapped_window_and_writes_plain_16khz_mono_pcm()
    {
        var window = new TurnFrameWindow(33, 81, 30);

        IReadOnlyList<string> video = NvidiaMaxineLipSynthesisEngine.BuildTurnVideoArguments("src.mp4", window, "turn.mp4");
        IReadOnlyList<string> audio = NvidiaMaxineLipSynthesisEngine.BuildTurnAudioArguments("dub.wav", window, "turn.wav");

        AssertOption(video, "-ss", "1.1");
        AssertOption(video, "-frames:v", "81");
        AssertOption(audio, "-ss", "1.1");
        AssertOption(audio, "-t", "2.7");
        AssertOption(audio, "-ar", "16000");
        AssertOption(audio, "-ac", "1");
        AssertOption(audio, "-c:a", "pcm_s16le");
    }

    [Fact]
    public void Patched_clip_is_cut_to_exactly_the_turn_frame_count()
    {
        IReadOnlyList<string> args = NvidiaMaxineLipSynthesisEngine.BuildPatchedClipArguments(
            "lipsync.avi", new TurnFrameWindow(33, 81, 30), "patched.mp4");

        AssertOption(args, "-frames:v", "81");
        Assert.Contains(args, arg => arg.Contains("tpad=stop_mode=clone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SynthesizeTurnAsync_runs_extract_host_and_encode_then_returns_the_patched_clip()
    {
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions(NvidiaMaxineLipSyncLanguage.Spanish);
        var runner = new FakeRunner();
        var engine = new NvidiaMaxineLipSynthesisEngine(options, runner, Environment.ProcessPath, Path.Join(root, "work"));

        LipSynthesisResult result = await engine.SynthesizeTurnAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(LipSynthesisEngineStatus.Synthesized, result.Status);
        Assert.True(File.Exists(result.PatchedClipPath));
        Assert.Equal(NvidiaMaxineLipSynthesisEngine.ProviderIdValue, result.ProviderId);
        RunnerCall host = Assert.Single(runner.Calls, call => call.ExecutablePath == options.HostExecutablePath);
        Assert.Contains("--language=3", host.Arguments);
        Assert.Equal("USE_APP_PATH", host.Options!.EnvironmentVariables!["NV_AR_SDK_PATH"]);
    }

    [Fact]
    public async Task SynthesizeTurnAsync_prefers_the_language_named_by_the_model_alias()
    {
        var runner = new FakeRunner();
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions(NvidiaMaxineLipSyncLanguage.Spanish);
        var engine = new NvidiaMaxineLipSynthesisEngine(options, runner, Environment.ProcessPath, Path.Join(root, "work"));

        await engine.SynthesizeTurnAsync(CreateRequest() with { PreferredModelAlias = "nvidia-maxine-lipsync-fr" }, CancellationToken.None);

        RunnerCall host = Assert.Single(runner.Calls, call => call.ExecutablePath == options.HostExecutablePath);
        Assert.Contains("--language=2", host.Arguments);
    }

    [Fact]
    public async Task SynthesizeTurnAsync_reports_a_failed_turn_when_the_host_exits_with_an_error()
    {
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions();
        var runner = new FakeRunner { HostExitCode = 1 };
        var engine = new NvidiaMaxineLipSynthesisEngine(options, runner, Environment.ProcessPath, Path.Join(root, "work"));

        LipSynthesisResult result = await engine.SynthesizeTurnAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(LipSynthesisEngineStatus.Failed, result.Status);
        Assert.Null(result.PatchedClipPath);
        Assert.Contains("Maxine LipSync failed with exit code 1", result.FailureReason);
    }

    [Fact]
    public async Task SynthesizeTurnAsync_skips_turns_shorter_than_the_lipsync_startup_window()
    {
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions();
        var runner = new FakeRunner();
        var engine = new NvidiaMaxineLipSynthesisEngine(options, runner, Environment.ProcessPath, Path.Join(root, "work"));

        // 9.926..10.005 s at 30 fps is 2 frames, the last turn of the EN->ES test clip.
        LipSynthesisResult result = await engine.SynthesizeTurnAsync(
            CreateRequest() with { TurnStart = TimeSpan.FromSeconds(9.926), TurnEnd = TimeSpan.FromSeconds(10.005) },
            CancellationToken.None);

        Assert.Equal(LipSynthesisEngineStatus.Skipped, result.Status);
        Assert.Contains("2 frames", result.SkipReason);
        Assert.DoesNotContain(runner.Calls, call => call.ExecutablePath == options.HostExecutablePath);
    }

    [Fact]
    public async Task SynthesizeTurnAsync_skips_without_running_anything_when_not_installed()
    {
        var runner = new FakeRunner();
        var engine = new NvidiaMaxineLipSynthesisEngine(
            new NvidiaMaxineLipSyncOptions(null, null), runner, Environment.ProcessPath, Path.Join(root, "work"));

        LipSynthesisResult result = await engine.SynthesizeTurnAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(LipSynthesisEngineStatus.Skipped, result.Status);
        Assert.Empty(runner.Calls);
    }

    private NvidiaMaxineLipSyncOptions CreateInstalledOptions(
        NvidiaMaxineLipSyncLanguage language = NvidiaMaxineLipSyncLanguage.Generic)
    {
        string host = Path.Join(root, "host", "LipSyncApp.exe");
        string sdk = Path.Join(root, "ARSDK");
        WriteFile(host);
        WriteFile(Path.Join(sdk, "bin", "nvARPose.dll"));
        Directory.CreateDirectory(Path.Join(sdk, "features", "nvarlipsync", "bin"));
        WriteFile(Path.Join(sdk, "bin", "models", "lipsync_fp16_100.engine.trtpkg"));
        return new NvidiaMaxineLipSyncOptions(host, sdk, language);
    }

    private static LipSynthesisRequest CreateRequest() =>
        new(
            OriginalVideoPath: "source.mp4",
            DubbedAudioPath: "dub.wav",
            SegmentId: Guid.NewGuid(),
            TurnStart: TimeSpan.FromSeconds(1.1),
            TurnEnd: TimeSpan.FromSeconds(3.8),
            SpeakerId: "speaker-1",
            Options: new LipSynthesisOptions());

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
    }

    private static void AssertOption(IReadOnlyList<string> args, string option, string expectedValue)
    {
        int index = Enumerable.Range(0, args.Count).FirstOrDefault(i => args[i] == option, -1);
        Assert.True(index >= 0 && index + 1 < args.Count, $"Expected option '{option}' with a value.");
        Assert.Equal(expectedValue, args[index + 1]);
    }

    private sealed record RunnerCall(string ExecutablePath, IReadOnlyList<string> Arguments, ProcessRunOptions? Options);

    /// <summary>Answers ffprobe with 30 fps and creates each step's output file, like the real tools.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public List<RunnerCall> Calls { get; } = [];

        public int HostExitCode { get; init; }

        public Task<ProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            ProcessRunOptions? options = null)
        {
            Calls.Add(new RunnerCall(executablePath, arguments.ToArray(), options));
            if (arguments.Contains("stream=avg_frame_rate,r_frame_rate"))
            {
                return Task.FromResult(new ProcessResult(0, "30/1\n30/1\n", string.Empty));
            }

            bool isHost = arguments.Any(arg => arg.StartsWith("--model_path=", StringComparison.Ordinal));
            if (isHost && HostExitCode != 0)
            {
                return Task.FromResult(new ProcessResult(HostExitCode, string.Empty, "ERROR: an error has occured"));
            }

            string output = isHost
                ? arguments.Single(arg => arg.StartsWith("--out=", StringComparison.Ordinal))["--out=".Length..]
                : arguments[^1];
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllBytes(output, [0]);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
