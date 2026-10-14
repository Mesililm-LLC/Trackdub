using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Infrastructure.Translation;

namespace Trackdub.Infrastructure.Tests;

public sealed class GeminiCloudTranslationEngineTests
{
    [Fact]
    public async Task TranslateAsync_sends_request_to_gemini_flash_and_parses_response()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          { "text": "```json\n[\"Hola\", \"Mundo\"]\n```" }
                        ]
                      }
                    }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            new StaticCloudEgressConsentService(true));

        IReadOnlyList<TranslatedTextSegment> result = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "es",
                [
                    new TranslationInputSegment(0, 0, 1.2, "Hello"),
                    new TranslationInputSegment(1, 1.2, 2.5, "World")
                ]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("models/gemini-3.8-flash:generateContent", handler.RequestUri.ToString());
        Assert.Equal("test-gemini-key", handler.ApiKeyHeader);
        Assert.Empty(handler.RequestUri.Query);
        Assert.Equal(2, result.Count);
        Assert.Equal("Hola", result[0].Text);
        Assert.Equal("Mundo", result[1].Text);
        Assert.Equal("gemini-3.8-flash", engine.LastExecutionMetadata?.ModelId);
        Assert.Equal("gemini-3.8-flash", engine.LastExecutionSummary?.ModelId);
    }

    [Fact]
    public async Task TranslateAsync_selects_lite_model_when_variant_is_lite()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          { "text": "[\"Hola\"]" }
                        ]
                      }
                    }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            new StaticCloudEgressConsentService(true));

        IReadOnlyList<TranslatedTextSegment> result = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "es",
                [new TranslationInputSegment(0, 0, 1.0, "Hello")],
                PreferredModelVariantAlias: "3.5-flash-lite"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("models/gemini-3.5-flash-lite:generateContent", handler.RequestUri.ToString());
        Assert.Equal("gemini-3.5-flash-lite", engine.LastExecutionMetadata?.ModelId);
    }

    [Fact]
    public async Task TranslateAsync_selects_pro_model_when_variant_is_pro()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          { "text": "[\"Hola\"]" }
                        ]
                      }
                    }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            new StaticCloudEgressConsentService(true));

        IReadOnlyList<TranslatedTextSegment> result = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "es",
                [new TranslationInputSegment(0, 0, 1.0, "Hello")],
                PreferredModelVariantAlias: "pro"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("models/gemini-2.5-pro:generateContent", handler.RequestUri.ToString());
        Assert.Equal("gemini-2.5-pro", engine.LastExecutionMetadata?.ModelId);
    }

    [Fact]
    public async Task TranslateAsync_includes_glossary_hints_in_prompt()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          { "text": "[\"Bonjour\"]" }
                        ]
                      }
                    }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            new StaticCloudEgressConsentService(true));

        await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "fr",
                [new TranslationInputSegment(0, 0, 1.0, "Hello")],
                GlossaryHints: [new TranslationGlossaryHint("Hello", "Bonjour", true)]),
            TestContext.Current.CancellationToken);

        Assert.Contains("Hello", handler.RequestBody);
        Assert.Contains("Bonjour", handler.RequestBody);
    }

    [Fact]
    public async Task TranslateAsync_throws_when_api_key_is_missing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var engine = new GeminiCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider(null),
            new StaticCloudEgressConsentService(true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.TranslateAsync(
                new TranslationRequest(
                    "en",
                    "es",
                    [new TranslationInputSegment(0, 0, 1.0, "Hello")]),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TranslateAsync_throws_CloudEgressConsentException_when_consent_not_granted()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("Network must not be called without consent."));
        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"),
            new StaticCloudEgressConsentService(false));

        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            engine.TranslateAsync(
                new TranslationRequest(
                    "en",
                    "es",
                    [new TranslationInputSegment(0, 0, 1.0, "Hello")]),
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
        public string? ApiKeyHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            if (request.Headers.TryGetValues("x-goog-api-key", out IEnumerable<string>? values))
            {
                ApiKeyHeader = values.FirstOrDefault();
            }

            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return responseFactory(request);
        }
    }
}
