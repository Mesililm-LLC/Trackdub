using System.Text.Json;

using Trackdub.Cli;
using Trackdub.Cli.Commands;
using Trackdub.Cli.Handlers;
using Trackdub.Contracts.Serving;
using Trackdub.Domain;

namespace Trackdub.Sdk.Tests;

public sealed class ServeCommandTests : IDisposable
{
    private readonly string directory = Path.Join(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));

    public ServeCommandTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task RunLoop_AnswersEachLineAndExitsCleanlyOnEof()
    {
        using var host = new FakeHost();
        using var output = new StringWriter();
        string requests = string.Join('\n',
            """{"tensors":[{"name":"x","type":"float32","shape":[2],"data":[1.5,2]}]}""",
            "",
            "not json",
            """{"id":7,"op":"health"}""",
            """{"id":"u","op":"nope"}""");

        int exitCode = await ServeHandler.RunLoopAsync(host, new StringReader(requests), output, CancellationToken.None);

        Assert.Equal(Program.ExitSuccess, exitCode);
        JsonElement[] responses = ParseLines(output.ToString());
        Assert.Equal(4, responses.Length);

        Assert.Equal("ok", responses[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, responses[0].GetProperty("id").ValueKind);
        JsonElement tensor = responses[0].GetProperty("outputs")[0];
        Assert.Equal("float32", tensor.GetProperty("type").GetString());
        Assert.Equal([1.5, 2.0], tensor.GetProperty("data").EnumerateArray().Select(static item => item.GetDouble()));

        Assert.Equal("invalid-json", responses[1].GetProperty("reason").GetString());

        Assert.Equal(7, responses[2].GetProperty("id").GetInt32());
        Assert.Equal("alive", responses[2].GetProperty("status").GetString());
        Assert.Equal("m", responses[2].GetProperty("models")[0].GetProperty("model").GetString());

        Assert.Equal("u", responses[3].GetProperty("id").GetString());
        Assert.Equal("unknown-op", responses[3].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Infer_HostErrorsKeepTheirReason()
    {
        using var host = new FakeHost { RunError = new ResidentInferenceException("infer-failed", "device lost") };

        JsonElement response = await HandleAsync(host, """{"id":1,"tensors":[]}""");

        Assert.Equal("error", response.GetProperty("status").GetString());
        Assert.Equal("infer-failed", response.GetProperty("reason").GetString());
        Assert.Equal("device lost", response.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("""{"tensors":[{"name":"x","type":"complex64","shape":[1],"data":[1]}]}""")]
    [InlineData("""{"tensors":[{"name":"x","type":"int8","shape":[1],"data":[300]}]}""")]
    [InlineData("""{"tensors":[{"name":"x","type":"float32","shape":["a"],"data":[1]}]}""")]
    [InlineData("""{"tensors":[{"name":"x","shape":[1],"data":[1]}]}""")]
    [InlineData("""{"model":"m"}""")]
    public async Task Infer_MalformedTensors_ReportBadInputs(string request)
    {
        using var host = new FakeHost();

        JsonElement response = await HandleAsync(host, request);

        Assert.Equal("bad-inputs", response.GetProperty("reason").GetString());
        Assert.Equal(0, host.Runs);
    }

    [Fact]
    public async Task Infer_ParsesEveryTypeIntoItsExactArrayType()
    {
        using var host = new FakeHost();

        await HandleAsync(host, """
            {"tensors":[
              {"name":"a","type":"float16","shape":[1],"data":[0.5]},
              {"name":"b","type":"int64","shape":[1],"data":[9007199254740993]},
              {"name":"c","dtype":"uint8","shape":[1],"data":[255]},
              {"name":"d","type":"int8","shape":[1],"data":[-1]},
              {"name":"e","type":"bool","shape":[2],"data":[true,0]}]}
            """.ReplaceLineEndings(""));

        IReadOnlyList<ServeTensor> inputs = Assert.Single(host.Received);
        Assert.IsType<Half[]>(inputs[0].Data);
        Assert.Equal(9007199254740993L, ((long[])inputs[1].Data)[0]);
        Assert.IsType<byte[]>(inputs[2].Data);
        Assert.IsType<sbyte[]>(inputs[3].Data);
        Assert.Equal([true, false], (bool[])inputs[4].Data);
    }

    [Fact]
    public async Task Infer_SeveralModelsLoaded_RequiresAModelName()
    {
        using var host = new FakeHost();
        host.Models.Add("second");

        JsonElement response = await HandleAsync(host, """{"tensors":[]}""");

        Assert.Equal("bad-inputs", response.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Load_ParsesProviderRequireAndTrtRtxOptions()
    {
        using var host = new FakeHost();

        JsonElement response = await HandleAsync(host,
            """{"id":"l","op":"load","model":"enc","path":"enc.onnx","provider":"openvino-catalog","require":true,"trtRtxOptions":{"k":"v"}}""");

        Assert.Equal("loaded", response.GetProperty("status").GetString());
        ResidentModelSpec spec = Assert.Single(host.Loads);
        Assert.Equal(("enc", "enc.onnx", ExecutionProviderKind.OpenVinoCatalog, true), (spec.Name, spec.ModelPath, spec.PreferredProvider, spec.RequireProvider));
        Assert.Equal("v", spec.ProviderOptions!["k"]);
    }

    [Theory]
    [InlineData("""{"op":"load","model":"m","path":"p","provider":"warp-drive"}""")]
    [InlineData("""{"op":"load","model":"m","path":"p","require":true}""")]
    [InlineData("""{"op":"load","path":"p"}""")]
    public async Task Load_InvalidRequest_ReportsBadInputs(string request)
    {
        using var host = new FakeHost();

        JsonElement response = await HandleAsync(host, request);

        Assert.Equal("bad-inputs", response.GetProperty("reason").GetString());
        Assert.Empty(host.Loads);
    }

    [Fact]
    public async Task Unload_UnknownModel_ReportsUnknownModel()
    {
        using var host = new FakeHost();

        JsonElement response = await HandleAsync(host, """{"op":"unload","model":"ghost"}""");

        Assert.Equal("unknown-model", response.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Serve_LoadFailure_ExitsWithPipelineFailureAndWritesNothingToStdout()
    {
        using var host = new FakeHost { LoadError = new ResidentInferenceException("provider-unavailable", "no GPU") };
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();

        int exitCode = await ServeHandler.ServeAsync(
            host, [new ServeModelArgument("m", "m.onnx")], ExecutionProviderKind.TensorRTRtx, requireProvider: true,
            new Dictionary<string, string>(), new Dictionary<string, string>(), autoWarmup: true,
            new StringReader(""), output, diagnostics, CancellationToken.None);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("provider-unavailable", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_FailedAutoWarmupOnlyWarns()
    {
        using var host = new FakeHost { RunError = new ResidentInferenceException("bad-inputs", "zeros rejected") };
        using var diagnostics = new StringWriter();

        int exitCode = await ServeHandler.ServeAsync(
            host, [new ServeModelArgument("m", "m.onnx")], provider: null, requireProvider: false,
            new Dictionary<string, string>(), new Dictionary<string, string>(), autoWarmup: true,
            new StringReader(""), new StringWriter(), diagnostics, CancellationToken.None);

        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.Contains("auto-warmup for 'm' failed", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_FailedExplicitWarmupStopsTheServer()
    {
        string warmup = Path.Join(directory, "warmup.json");
        await File.WriteAllTextAsync(warmup, """{"tensors":[]}""", CancellationToken.None);
        using var host = new FakeHost { RunError = new ResidentInferenceException("infer-failed", "boom") };

        int exitCode = await ServeHandler.ServeAsync(
            host, [new ServeModelArgument("m", "m.onnx")], provider: null, requireProvider: false,
            new Dictionary<string, string>(), new Dictionary<string, string> { ["m"] = warmup }, autoWarmup: true,
            new StringReader(""), new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);
    }

    [Fact]
    public void ModelArguments_DefaultTheNameAndRejectDuplicatesAndMissingFiles()
    {
        string first = Path.Join(directory, "encoder.onnx");
        string second = Path.Join(directory, "decoder.onnx");
        File.WriteAllBytes(first, []);
        File.WriteAllBytes(second, []);

        Assert.True(ServeCommand.TryParseNamedPaths([first, $"dec={second}"], "--model", out List<ServeModelArgument> models));
        Assert.Equal([new ServeModelArgument("encoder", first), new ServeModelArgument("dec", second)], models);

        Assert.False(ServeCommand.TryParseNamedPaths([first, $"encoder={second}"], "--model", out _));
        Assert.False(ServeCommand.TryParseNamedPaths([Path.Join(directory, "absent.onnx")], "--model", out _));
    }

    [Fact]
    public void WarmupArguments_UnnamedAppliesToEveryModel()
    {
        string warmup = Path.Join(directory, "warmup.json");
        File.WriteAllText(warmup, "{}");
        ServeModelArgument[] models = [new("a", "a.onnx"), new("b", "b.onnx")];

        Assert.True(ServeCommand.TryParseWarmupFiles([warmup], models, out Dictionary<string, string> all));
        Assert.Equal(["a", "b"], all.Keys.Order());
        Assert.False(ServeCommand.TryParseWarmupFiles([$"c={warmup}"], models, out _));
    }

    [Fact]
    public void ServeCommand_IsRegisteredOnTheRoot() =>
        Assert.Contains(Program.BuildRootCommand().Subcommands, static command => command.Name == "serve");

    private static async Task<JsonElement> HandleAsync(FakeHost host, string request) =>
        JsonDocument.Parse(await ServeHandler.HandleLineAsync(host, request, CancellationToken.None)).RootElement;

    private static JsonElement[] ParseLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => JsonDocument.Parse(line).RootElement)
            .ToArray();

    private sealed class FakeHost : IResidentInferenceHost
    {
        public List<string> Models { get; } = ["m"];
        public List<ResidentModelSpec> Loads { get; } = [];
        public List<IReadOnlyList<ServeTensor>> Received { get; } = [];
        public ResidentInferenceException? LoadError { get; init; }
        public ResidentInferenceException? RunError { get; init; }
        public int Runs { get; private set; }

        public Task<ResidentModelInfo> LoadAsync(ResidentModelSpec spec, CancellationToken cancellationToken = default)
        {
            if (LoadError is not null)
            {
                throw LoadError;
            }

            Loads.Add(spec);
            return Task.FromResult(Info(spec.Name));
        }

        public Task<ResidentRunResult> RunAsync(
            string modelName,
            IReadOnlyList<ServeTensor> inputs,
            CancellationToken cancellationToken = default)
        {
            Runs++;
            if (RunError is not null)
            {
                throw RunError;
            }

            Received.Add(inputs);
            return Task.FromResult(new ResidentRunResult(modelName, "cpu", inputs, TimeSpan.FromMilliseconds(1)));
        }

        public async Task<ResidentRunResult?> WarmAsync(
            string modelName,
            IReadOnlyList<ServeTensor>? inputs = null,
            CancellationToken cancellationToken = default) =>
            await RunAsync(modelName, inputs ?? [], cancellationToken);

        public Task<bool> UnloadAsync(string modelName) => Task.FromResult(Models.Remove(modelName));

        public IReadOnlyList<ResidentModelInfo> Describe() => Models.Select(Info).ToList();

        public void Dispose()
        {
        }

        private static ResidentModelInfo Info(string name) =>
            new(name, $"{name}.onnx", "cpu", "cpu", [], null, [], [], Poisoned: false);
    }
}
