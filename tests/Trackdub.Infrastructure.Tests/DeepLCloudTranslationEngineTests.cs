using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Infrastructure.Translation;

namespace Trackdub.Infrastructure.Tests;

public sealed class DeepLCloudTranslationEngineTests
{
    [Fact]
    public async Task TranslateAsync_sends_json_request_to_free_endpoint_and_maps_segments()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "translations": [
                    { "detected_source_language": "EN", "text": "Hola" },
                    { "detected_source_language": "EN", "text": "Mundo" }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var engine = new DeepLCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-key:fx"),
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

        Assert.Equal(new Uri("https://api-free.deepl.com/v2/translate"), handler.RequestUri);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new AuthenticationHeaderValue("DeepL-Auth-Key", "test-key:fx"), handler.Authorization);
        using JsonDocument body = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("ES", body.RootElement.GetProperty("target_lang").GetString());
        Assert.Equal("EN", body.RootElement.GetProperty("source_lang").GetString());
        Assert.Equal(
            ["Hello", "World"],
            body.RootElement.GetProperty("text").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray());
        Assert.Collection(
            result,
            segment =>
            {
                Assert.Equal(0, segment.Index);
                Assert.Equal(0, segment.StartSeconds);
                Assert.Equal(1.2, segment.EndSeconds);
                Assert.Equal("Hola", segment.Text);
            },
            segment =>
            {
                Assert.Equal(1, segment.Index);
                Assert.Equal(1.2, segment.StartSeconds);
                Assert.Equal(2.5, segment.EndSeconds);
                Assert.Equal("Mundo", segment.Text);
            });
        Assert.Equal("deepl", engine.LastExecutionMetadata?.ProviderName);
        Assert.Equal("cloud", engine.LastExecutionSummary?.SelectedProvider);
    }

    [Fact]
    public async Task TranslateAsync_omits_source_language_when_source_is_auto()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                { "translations": [ { "detected_source_language": "EN", "text": "Hola" } ] }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var engine = new DeepLCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-key"),
            new StaticCloudEgressConsentService(true));

        await engine.TranslateAsync(
            new TranslationRequest(
                "auto",
                "es",
                [new TranslationInputSegment(0, 0, 1, "Hello")]),
            TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://api.deepl.com/v2/translate"), handler.RequestUri);
        using JsonDocument body = JsonDocument.Parse(handler.RequestBody);
        Assert.False(body.RootElement.TryGetProperty("source_lang", out _));
    }

    [Fact]
    public async Task TranslateAsync_without_api_key_fails_before_network_request()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("Network should not be called."));
        using var httpClient = new HttpClient(handler);
        var engine = new DeepLCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider(null),
            new StaticCloudEgressConsentService(true));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.TranslateAsync(
                new TranslationRequest(
                    "en",
                    "es",
                    [new TranslationInputSegment(0, 0, 1, "Hello")]),
                TestContext.Current.CancellationToken));

        Assert.Contains("DeepL API key is not configured", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_non_success_response_includes_status_and_response_body()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage((HttpStatusCode)456)
        {
            Content = JsonContent("""{ "message": "Quota exceeded" }""")
        });
        using var httpClient = new HttpClient(handler);
        var engine = new DeepLCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-key:fx"),
            new StaticCloudEgressConsentService(true));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.TranslateAsync(
                new TranslationRequest(
                    "en",
                    "es",
                    [new TranslationInputSegment(0, 0, 1, "Hello")]),
                TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 456", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Quota exceeded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_throws_when_deepl_returns_wrong_segment_count()
    {
        // DeepL returned 1 translation for 2 input segments — engine must not silently drop data.
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                { "translations": [ { "detected_source_language": "EN", "text": "Hola" } ] }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var engine = new DeepLCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-key:fx"),
            new StaticCloudEgressConsentService(true));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.TranslateAsync(
                new TranslationRequest(
                    "en",
                    "es",
                    [
                        new TranslationInputSegment(0, 0, 1.0, "Hello"),
                        new TranslationInputSegment(1, 1.0, 2.0, "World")
                    ]),
                TestContext.Current.CancellationToken));

        Assert.Contains("1 translation result(s) for 2 input segment(s)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_without_consent_throws_CloudEgressConsentException_before_network_request()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("Network should not be called."));
        using var httpClient = new HttpClient(handler);
        var engine = new DeepLCloudTranslationEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-key:fx"),
            new StaticCloudEgressConsentService(false));

        await Assert.ThrowsAsync<CloudEgressConsentException>(() =>
            engine.TranslateAsync(
                new TranslationRequest(
                    "en",
                    "es",
                    [new TranslationInputSegment(0, 0, 1, "Hello")]),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.CallCount);
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

    private sealed class StaticCloudEgressConsentService(bool consented) : ICloudEgressConsentService
    {
        public bool HasConsent(string consentKey) => consented && string.Equals(consentKey, CloudEgressConsentKeys.TextDeepL, StringComparison.OrdinalIgnoreCase);
        public IReadOnlyList<string> GetRequiredConsentKeys(string stageName, string? modelAlias) => [CloudEgressConsentKeys.TextDeepL];
        public Task SetConsentAsync(string consentKey, bool value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StaticCloudApiKeyProvider(string? apiKey) : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult(apiKey);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public Uri? RequestUri { get; private set; }

        public HttpMethod? Method { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            Method = request.Method;
            Authorization = request.Headers.Authorization;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return responder(request);
        }
    }
}
