using System.Net;
using System.Text;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Infrastructure.Transcripts;

namespace Trackdub.Infrastructure.Tests;

public sealed class GeminiCloudTextRefinementEngineTests
{
    [Fact]
    public async Task RefineAsync_sends_prompt_and_parses_refined_segments()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          {
                            "text": "```json\n[{\"index\": 0, \"start\": 0.0, \"end\": 1.5, \"text\": \"Hello world.\"}]\n```"
                          }
                        ]
                      }
                    }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTextRefinementEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            new StaticCloudEgressConsentService(true));

        IReadOnlyList<RefinedTextSegment> result = await engine.RefineAsync(
            new TextRefinementRequest(
                [new TextRefinementInputSegment(0, 0.0, 1.5, "hello world")],
                SourceLanguage: "en"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("models/gemini-3.5-flash-lite:generateContent", handler.RequestUri.ToString());
        Assert.Single(result);
        Assert.Equal("Hello world.", result[0].RefinedText);
        Assert.Equal("gemini-refinement-cloud", engine.EngineFamily);
        Assert.Equal("cloud", engine.LastExecutionSummary?.SelectedProvider);
        Assert.Equal("gemini-3.5-flash-lite", engine.LastExecutionSummary?.ModelId);
    }

    [Fact]
    public async Task RefineAsync_selects_advanced_model_when_variant_is_3_8()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          { "text": "[\"Hello universe.\"]" }
                        ]
                      }
                    }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTextRefinementEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            new StaticCloudEgressConsentService(true));

        IReadOnlyList<RefinedTextSegment> result = await engine.RefineAsync(
            new TextRefinementRequest(
                [new TextRefinementInputSegment(0, 0.0, 1.5, "hello universe")],
                SourceLanguage: "en",
                PreferredModelVariantAlias: "gemini-3.8-flash"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("models/gemini-3.8-flash:generateContent", handler.RequestUri.ToString());
        Assert.Equal("gemini-3.8-flash", engine.LastExecutionSummary?.ModelId);
    }

    [Fact]
    public async Task RefineAsync_throws_when_api_key_is_missing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var engine = new GeminiCloudTextRefinementEngine(
            httpClient,
            new StaticCloudApiKeyProvider(null),
            new StaticCloudEgressConsentService(true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RefineAsync(
                new TextRefinementRequest(
                    [new TextRefinementInputSegment(0, 0.0, 1.5, "hello world")],
                    SourceLanguage: "en"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefineAsync_throws_CloudEgressConsentException_when_consent_not_granted()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("Network must not be called without consent."));
        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTextRefinementEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            new StaticCloudEgressConsentService(false));

        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            engine.RefineAsync(
                new TextRefinementRequest(
                    [new TextRefinementInputSegment(0, 0.0, 1.5, "hello world")],
                    SourceLanguage: "en"),
                TestContext.Current.CancellationToken));

        Assert.Null(handler.RequestUri);
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class StaticCloudEgressConsentService(bool consented) : ICloudEgressConsentService
    {
        public bool HasConsent(string consentKey) => consented && string.Equals(consentKey, CloudEgressConsentKeys.TextGemini, StringComparison.OrdinalIgnoreCase);
        public IReadOnlyList<string> GetRequiredConsentKeys(string stageName, string? modelAlias) => [CloudEgressConsentKeys.TextGemini];
        public Task SetConsentAsync(string consentKey, bool value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StaticCloudApiKeyProvider(string? apiKey) : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult(apiKey);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return responseFactory(request);
        }
    }
}
