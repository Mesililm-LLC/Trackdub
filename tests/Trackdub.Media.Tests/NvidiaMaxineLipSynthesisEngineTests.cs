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
    public void Options_prefer_settings_and_fall_back_to_environment_variables_per_value()
    {
        var environment = new Dictionary<string, string>
        {
            [NvidiaMaxineLipSyncOptions.HostExecutableEnvironmentVariable] = @"C:\env\LipSyncApp.exe",
            [NvidiaMaxineLipSyncOptions.ArSdkRootEnvironmentVariable] = @"C:\env\ARSDK",
            [NvidiaMaxineLipSyncOptions.LanguageEnvironmentVariable] = "de",
        };
        string? Lookup(string name) => environment.TryGetValue(name, out string? value) ? value : null;

        NvidiaMaxineLipSyncOptions fromSettings = NvidiaMaxineLipSyncOptions.Resolve(@"D:\app\host.exe", "  ", "es", Lookup);
        NvidiaMaxineLipSyncOptions fromEnvironment = NvidiaMaxineLipSyncOptions.Resolve(null, null, null, Lookup);
        NvidiaMaxineLipSyncOptions unset = NvidiaMaxineLipSyncOptions.Resolve(null, null, "unknown", _ => null);

        Assert.Equal(@"D:\app\host.exe", fromSettings.HostExecutablePath);
        Assert.Equal(@"C:\env\ARSDK", fromSettings.ArSdkRoot);
        Assert.Equal(NvidiaMaxineLipSyncLanguage.Spanish, fromSettings.DefaultLanguage);
        Assert.Equal(@"C:\env\LipSyncApp.exe", fromEnvironment.HostExecutablePath);
        Assert.Equal(NvidiaMaxineLipSyncLanguage.German, fromEnvironment.DefaultLanguage);
        Assert.Null(unset.HostExecutablePath);
        Assert.Equal(NvidiaMaxineLipSyncLanguage.Generic, unset.DefaultLanguage);
    }

    [Fact]
    public void Engine_rereads_its_options_so_settings_changes_apply_without_a_restart()
    {
        var current = new NvidiaMaxineLipSyncOptions(null, null);
        var engine = new NvidiaMaxineLipSynthesisEngine(() => current);
        Assert.False(engine.IsAvailable);

        current = CreateInstalledOptions();

        Assert.True(engine.IsAvailable);
        Assert.Null(engine.UnavailableReason);
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
    public void Streaming_decode_and_encode_carry_exactly_the_turn_frames_as_raw_bgr24()
    {
        var window = new TurnFrameWindow(33, 81, 30);

        IReadOnlyList<string> decode = NvidiaMaxineLipSynthesisEngine.BuildFrameDecodeArguments("source.mp4", window);
        IReadOnlyList<string> encode = NvidiaMaxineLipSynthesisEngine.BuildFrameEncodeArguments(704, 1280, window, "patched.mp4");

        Assert.Equal("-nostdin", decode[0]);
        AssertOption(decode, "-ss", "1.1");
        AssertOption(decode, "-frames:v", "81");
        AssertOption(decode, "-pix_fmt", "bgr24");
        Assert.Equal("-", decode[^1]);
        AssertOption(encode, "-s", "704x1280");
        AssertOption(encode, "-r", "30");
        AssertOption(encode, "-i", "-");
        AssertOption(encode, "-frames:v", "81");
        Assert.Equal(Path.GetFullPath("patched.mp4"), encode[^1]);
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
        RunnerCall host = Assert.Single(HostRuns(runner, options));
        Assert.Contains("--language=3", host.Arguments);
        Assert.Equal("USE_APP_PATH", host.Options!.EnvironmentVariables!["NV_AR_SDK_PATH"]);
    }

    [Fact]
    public async Task SynthesizeTurnAsync_removes_the_turn_directory_and_hands_over_a_standalone_clip()
    {
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions();
        string work = Path.Join(root, "work");
        var engine = new NvidiaMaxineLipSynthesisEngine(options, new FakeRunner(), Environment.ProcessPath, work);
        LipSynthesisRequest request = CreateRequest();

        LipSynthesisResult result = await engine.SynthesizeTurnAsync(request, CancellationToken.None);

        Assert.Equal(LipSynthesisEngineStatus.Synthesized, result.Status);
        Assert.True(File.Exists(result.PatchedClipPath));
        Assert.False(Directory.Exists(Path.Join(work, request.SegmentId.ToString("N"))));
        Assert.Equal(Path.GetFullPath(work), Path.GetDirectoryName(Path.GetFullPath(result.PatchedClipPath!)));
    }

    [Fact]
    public async Task SynthesizeTurnAsync_leaves_nothing_behind_when_the_turn_is_skipped_or_fails()
    {
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions();
        string work = Path.Join(root, "work");
        var failing = new NvidiaMaxineLipSynthesisEngine(options, new FakeRunner { HostExitCode = 1 }, Environment.ProcessPath, work);
        var skipping = new NvidiaMaxineLipSynthesisEngine(options, new FakeRunner(), Environment.ProcessPath, work);

        LipSynthesisResult failed = await failing.SynthesizeTurnAsync(CreateRequest(), CancellationToken.None);
        LipSynthesisResult skipped = await skipping.SynthesizeTurnAsync(
            CreateRequest() with { TurnStart = TimeSpan.FromSeconds(9.926), TurnEnd = TimeSpan.FromSeconds(10.005) },
            CancellationToken.None);

        Assert.Equal(LipSynthesisEngineStatus.Failed, failed.Status);
        Assert.Equal(LipSynthesisEngineStatus.Skipped, skipped.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public async Task SynthesizeTurnAsync_streams_the_turn_through_a_persistent_host_when_it_answers_the_probe()
    {
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions(NvidiaMaxineLipSyncLanguage.Spanish);
        var runner = new FakeRunner { ProtocolProbeReply = new ProcessResult(0, MaxineLipSyncHostSession.Handshake + "\n", string.Empty) };
        using var host = ScriptedHost.Echo(lagFrames: 5);
        using var decoder = FakePipedProcess.Decoder(frames: 81, frameBytes: 64 * 48 * 3);
        using var encoder = FakePipedProcess.Encoder();
        int hostLaunches = 0;
        using var engine = new NvidiaMaxineLipSynthesisEngine(
            () => options, runner, Environment.ProcessPath, Path.Join(root, "work"),
            Launcher(options, host, decoder, encoder, () => hostLaunches++));

        LipSynthesisResult result = await engine.SynthesizeTurnAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(LipSynthesisEngineStatus.Synthesized, result.Status);
        Assert.Equal(1, hostLaunches);
        Assert.Empty(HostRuns(runner, options));
        Assert.Equal(81 * 64 * 48 * 3, encoder.Written.Length);
        Assert.True(File.Exists(result.PatchedClipPath));
        Assert.Contains("RUN 64 48 30 3 81 ", host.Requests.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SynthesizeTurnAsync_fails_the_turn_when_the_frame_decoder_exits_with_an_error()
    {
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions();
        var runner = new FakeRunner { ProtocolProbeReply = new ProcessResult(0, MaxineLipSyncHostSession.Handshake + "\n", string.Empty) };
        using var host = ScriptedHost.Echo(lagFrames: 0);
        using var decoder = FakePipedProcess.Decoder(frames: 10, frameBytes: 64 * 48 * 3, exitCode: 1, errorTail: "moov atom not found");
        using var encoder = FakePipedProcess.Encoder();
        using var engine = new NvidiaMaxineLipSynthesisEngine(
            () => options, runner, Environment.ProcessPath, Path.Join(root, "work"),
            Launcher(options, host, decoder, encoder));

        LipSynthesisResult result = await engine.SynthesizeTurnAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(LipSynthesisEngineStatus.Failed, result.Status);
        Assert.Contains("decode turn video failed with exit code 1", result.FailureReason);
        Assert.Contains("moov atom not found", result.FailureReason);
        Assert.Null(result.PatchedClipPath);
        Assert.Empty(Directory.EnumerateFiles(Path.Join(root, "work"), "patched-*"));
    }

    [Fact]
    public async Task Disposing_the_engine_during_a_turn_stops_the_host_instead_of_waiting_for_the_timeout()
    {
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions();
        var runner = new FakeRunner { ProtocolProbeReply = new ProcessResult(0, MaxineLipSyncHostSession.Handshake + "\n", string.Empty) };
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Answers the handshake and the RUN header, then never returns a frame, like a wedged GPU.
        using var host = new ScriptedHost(async (input, output, token) =>
        {
            await output.WriteAsync(System.Text.Encoding.ASCII.GetBytes(MaxineLipSyncHostSession.Handshake + "\n"), token);
            await ScriptedHost.ReadLineAsync(input);
            requestSeen.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        using var decoder = FakePipedProcess.Decoder(frames: 81, frameBytes: 64 * 48 * 3);
        using var encoder = FakePipedProcess.Encoder();
        var engine = new NvidiaMaxineLipSynthesisEngine(
            () => options, runner, Environment.ProcessPath, Path.Join(root, "work"),
            Launcher(options, host, decoder, encoder));
        Task<LipSynthesisResult> turn = engine.SynthesizeTurnAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Task dispose = Task.Run(engine.Dispose, TestContext.Current.CancellationToken);

        await dispose.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        LipSynthesisResult result = await turn.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        Assert.True(host.Killed);
        Assert.Equal(LipSynthesisEngineStatus.Failed, result.Status);
    }

    [Fact]
    public async Task A_turn_that_starts_its_host_after_Dispose_gave_up_still_stops_it()
    {
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions();
        var runner = new FakeRunner { ProtocolProbeReply = new ProcessResult(0, MaxineLipSyncHostSession.Handshake + "\n", string.Empty) };
        using var host = ScriptedHost.Echo(lagFrames: 0);
        using var decoder = FakePipedProcess.Decoder(frames: 81, frameBytes: 64 * 48 * 3);
        using var encoder = FakePipedProcess.Encoder();
        NvidiaMaxineLipSynthesisEngine? engine = null;
        Task? dispose = null;
        PipedProcessLauncher inner = Launcher(options, host, decoder, encoder);
        engine = new NvidiaMaxineLipSynthesisEngine(
            () => options, runner, Environment.ProcessPath, Path.Join(root, "work"),
            (path, arguments, environment, redirectInput, redirectOutput) =>
            {
                if (path == options.HostExecutablePath)
                {
                    // Dispose runs while the host is still starting: it sees no session to stop, and gives up
                    // waiting for the turn long before the host finishes starting.
                    dispose = Task.Run(engine!.Dispose);
                    Assert.True(SpinWait.SpinUntil(() => engine.IsDisposed, TimeSpan.FromSeconds(10)));
                    Thread.Sleep(500);
                }

                return inner(path, arguments, environment, redirectInput, redirectOutput);
            })
        {
            DisposeWaitLimit = TimeSpan.FromMilliseconds(100),
        };

        await engine.SynthesizeTurnAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await dispose!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(host.Disposed);
    }

    [Fact]
    public async Task Disposing_the_engine_after_a_persistent_turn_does_not_wedge_it()
    {
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions();
        var runner = new FakeRunner { ProtocolProbeReply = new ProcessResult(0, MaxineLipSyncHostSession.Handshake + "\n", string.Empty) };
        using var host = ScriptedHost.Echo(lagFrames: 0);
        using var decoder = FakePipedProcess.Decoder(frames: 81, frameBytes: 64 * 48 * 3);
        using var encoder = FakePipedProcess.Encoder();
        var engine = new NvidiaMaxineLipSynthesisEngine(
            () => options, runner, Environment.ProcessPath, Path.Join(root, "work"),
            Launcher(options, host, decoder, encoder));
        await engine.SynthesizeTurnAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Task dispose = Task.Run(engine.Dispose, TestContext.Current.CancellationToken);

        await dispose.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(host.Disposed);
    }

    [Fact]
    public async Task SynthesizeTurnAsync_prefers_the_language_named_by_the_model_alias()
    {
        var runner = new FakeRunner();
        NvidiaMaxineLipSyncOptions options = CreateInstalledOptions(NvidiaMaxineLipSyncLanguage.Spanish);
        var engine = new NvidiaMaxineLipSynthesisEngine(options, runner, Environment.ProcessPath, Path.Join(root, "work"));

        await engine.SynthesizeTurnAsync(CreateRequest() with { PreferredModelAlias = "nvidia-maxine-lipsync-fr" }, CancellationToken.None);

        RunnerCall host = Assert.Single(HostRuns(runner, options));
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
        Assert.Empty(HostRuns(runner, options));
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

    private static IEnumerable<RunnerCall> HostRuns(FakeRunner runner, NvidiaMaxineLipSyncOptions options) =>
        runner.Calls.Where(call => call.ExecutablePath == options.HostExecutablePath && !call.Arguments.Contains("--protocol"));

    private static PipedProcessLauncher Launcher(
        NvidiaMaxineLipSyncOptions options,
        ScriptedHost host,
        FakePipedProcess decoder,
        FakePipedProcess encoder,
        Action? onHostLaunch = null) =>
        (path, arguments, _, redirectInput, _) =>
        {
            if (path == options.HostExecutablePath)
            {
                onHostLaunch?.Invoke();
                return host;
            }

            if (!redirectInput)
            {
                return decoder;
            }

            encoder.OutputPath = arguments[^1];
            return encoder;
        };

    /// <summary>An FFmpeg stand-in: a decoder that yields raw frames, or an encoder that records its input.</summary>
    private sealed class FakePipedProcess : IPipedProcess
    {
        private readonly int exitCode;
        private readonly string errorTail;

        private FakePipedProcess(Stream input, Stream output, int exitCode, string errorTail, bool outputIsFrames)
        {
            Input = input;
            Output = output;
            this.exitCode = exitCode;
            this.errorTail = errorTail;
            OutputIsFrames = outputIsFrames;
        }

        public bool OutputIsFrames { get; }

        public string? OutputPath { get; set; }

        public MemoryStream Written { get; private init; } = new();

        public Stream Input { get; }

        public Stream Output { get; }

        public bool HasExited => true;

        public string ErrorTail => errorTail;

        public static FakePipedProcess Decoder(int frames, int frameBytes, int exitCode = 0, string errorTail = "")
        {
            byte[] data = new byte[frames * frameBytes];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 251);
            }

            return new FakePipedProcess(Stream.Null, new MemoryStream(data), exitCode, errorTail, outputIsFrames: true);
        }

        public static FakePipedProcess Encoder()
        {
            var written = new MemoryStream();
            return new FakePipedProcess(new NonClosingStream(written), Stream.Null, 0, string.Empty, outputIsFrames: false)
            {
                Written = written,
            };
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (!OutputIsFrames)
            {
                // The encoder writes its output file when its input closes, like FFmpeg.
                string path = OutputPath ?? string.Empty;
                if (path.Length > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, [0]);
                }
            }

            return Task.FromResult(exitCode);
        }

        public void Kill()
        {
        }

        // The engine disposes its processes when a turn ends, but tests read Written afterwards, and a
        // MemoryStream holds no unmanaged resources, so it is left to the garbage collector.
        public void Dispose()
        {
        }
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
    }

    private sealed record RunnerCall(string ExecutablePath, IReadOnlyList<string> Arguments, ProcessRunOptions? Options);

    /// <summary>Answers ffprobe with 30 fps and creates each step's output file, like the real tools.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public List<RunnerCall> Calls { get; } = [];

        public int HostExitCode { get; init; }

        /// <summary>LipSyncApp's answer to the persistent-host probe: it rejects the flag.</summary>
        public ProcessResult ProtocolProbeReply { get; init; } = new(100, "Unknown flag: \"--protocol\"", string.Empty);

        public Task<ProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            ProcessRunOptions? options = null)
        {
            Calls.Add(new RunnerCall(executablePath, arguments.ToArray(), options));
            if (arguments.Contains("--protocol"))
            {
                return Task.FromResult(ProtocolProbeReply);
            }

            if (arguments.Contains("stream=width,height:stream_side_data=rotation"))
            {
                return Task.FromResult(new ProcessResult(0, "width=64\nheight=48\n", string.Empty));
            }

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
