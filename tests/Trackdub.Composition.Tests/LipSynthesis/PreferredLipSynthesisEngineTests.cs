using Trackdub.Composition.LipSynthesis;
using Trackdub.Contracts.Pipeline;
using Trackdub.Media.LipSynthesis.NvidiaMaxine;

namespace Trackdub.Composition.Tests.LipSynthesis;

public sealed class PreferredLipSynthesisEngineTests
{
    private readonly StubEngine maxine = new(NvidiaMaxineLipSynthesisEngine.ProviderIdValue);
    private readonly StubEngine latentSync = new("latentsync-onnx");

    [Fact]
    public void Uses_the_first_available_engine_in_preference_order()
    {
        var router = new PreferredLipSynthesisEngine([maxine, latentSync]);

        Assert.Equal(NvidiaMaxineLipSynthesisEngine.ProviderIdValue, router.ProviderId);
        Assert.True(router.IsAvailable);
    }

    [Fact]
    public void Falls_back_to_the_next_engine_when_maxine_is_not_configured()
    {
        maxine.Available = false;
        var router = new PreferredLipSynthesisEngine([maxine, latentSync]);

        Assert.Equal("latentsync-onnx", router.ProviderId);
    }

    [Fact]
    public void Reports_unavailable_when_no_engine_is_available()
    {
        maxine.Available = false;
        latentSync.Available = false;
        var router = new PreferredLipSynthesisEngine([maxine, latentSync]);

        Assert.False(router.IsAvailable);
    }

    [Theory]
    [InlineData("latentsync-1.6", "latentsync-onnx")]
    [InlineData("nvidia-maxine-lipsync-es", NvidiaMaxineLipSynthesisEngine.ProviderIdValue)]
    [InlineData(null, NvidiaMaxineLipSynthesisEngine.ProviderIdValue)]
    public void Honors_a_model_alias_that_names_an_engine(string? alias, string expectedProvider)
    {
        var router = new PreferredLipSynthesisEngine([maxine, latentSync]);

        Assert.Equal(expectedProvider, router.Select(alias).ProviderId);
    }

    [Fact]
    public async Task Routes_synthesis_to_the_selected_engine()
    {
        var router = new PreferredLipSynthesisEngine([maxine, latentSync]);

        await router.SynthesizeTurnAsync(
            new LipSynthesisRequest("video.mp4", "dub.wav", Guid.NewGuid(), TimeSpan.Zero, TimeSpan.FromSeconds(1),
                "speaker", new LipSynthesisOptions()),
            CancellationToken.None);

        Assert.Equal(1, maxine.CallCount);
        Assert.Equal(0, latentSync.CallCount);
    }

    private sealed class StubEngine(string providerId) : ILipSynthesisEngine
    {
        public bool Available { get; set; } = true;

        public int CallCount { get; private set; }

        public bool IsAvailable => Available;

        public bool IsExperimental => false;

        public string ProviderId => providerId;

        public string ModelId => providerId + "-model";

        public Task<LipSynthesisResult> SynthesizeTurnAsync(LipSynthesisRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new LipSynthesisResult(
                request.SegmentId, LipSynthesisEngineStatus.Skipped, null, "stub", null, ProviderId, ModelId));
        }
    }
}
