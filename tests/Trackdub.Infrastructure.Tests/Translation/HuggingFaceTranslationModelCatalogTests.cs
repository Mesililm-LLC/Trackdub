using System.Net;
using Trackdub.Infrastructure.Translation;
using Xunit;

namespace Trackdub.Infrastructure.Tests.Translation;

public sealed class HuggingFaceTranslationModelCatalogTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PairPinsRevisionAndHashesMetadataWithoutDownloadingWeights(bool group, bool missingCandidate)
    {
        string directory = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString());
        string revision = new('a', 40);
        string hash = new('b', 64);
        string modelName = group ? "opus-mt-en-ROMANCE" : "opus-mt-en-fr";
        string[] paths = ["onnx/encoder_model.onnx", "onnx/decoder_model_merged.onnx", "config.json", "tokenizer_config.json", "vocab.json", "source.spm", "target.spm"];
        using var client = new HttpClient(new Handler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            string json;
            if (url.Contains("/api/models/onnx-community/opus-mt-de-en", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (url.Contains("author=onnx-community", StringComparison.Ordinal)) json = "[" +
                (missingCandidate ? "{\"id\":\"onnx-community/opus-mt-de-en\"}," : "") +
                "{\"id\":\"onnx-community/" + modelName + "\"}]";
            else if (url.Contains("author=Xenova", StringComparison.Ordinal)) json = "[]";
            else if (url.Contains("/api/models/Helsinki-NLP/", StringComparison.Ordinal)) json = System.Text.Json.JsonSerializer.Serialize(new { sha = revision, cardData = new { license = "apache-2.0" } });
            else if (url.Contains("/api/models/", StringComparison.Ordinal)) json = System.Text.Json.JsonSerializer.Serialize(new
            {
                sha = revision,
                cardData = group ? null : new { license = "apache-2.0", base_model = (string?)null },
                siblings = paths.Select(path => new { rfilename = path, size = 123, lfs = path.EndsWith(".onnx", StringComparison.Ordinal) ? new { sha256 = hash } : null })
            });
            else
            {
                Assert.DoesNotContain(".onnx", url);
                Assert.Contains(revision, url);
                json = url.EndsWith("README.md", StringComparison.Ordinal) ? "* source languages: eng\n* target languages: fra spa fr_BE pt_br\n"
                    : url.EndsWith("vocab.json", StringComparison.Ordinal) ? "{\">>fra<<\":1}"
                    : System.Text.Json.JsonSerializer.Serialize(new { model_type = "marian", _name_or_path = group ? "Helsinki-NLP/" + modelName : null });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        try
        {
            var catalog = new HuggingFaceTranslationModelCatalog(client, Path.Join(directory, "catalog.json"));
            var snapshot = await catalog.RefreshAsync(false, TestContext.Current.CancellationToken);
            var model = Assert.Single(snapshot.Models);
            Assert.Equal("en", model.SourceLanguage);
            Assert.Equal("fr", model.TargetLanguage);
            Assert.Equal(revision, model.Revision);
            Assert.Equal(group, model.IsMultilingual);
            Assert.Equal(group ? ">>fra<<" : null, model.TargetPrefix);
            Assert.Equal(7, model.Files.Length);
            Assert.All(model.Files, file => Assert.Equal(64, file.Sha256.Length));
            if (missingCandidate)
                Assert.Equal("onnx-community/opus-mt-de-en", Assert.Single(snapshot.Exclusions).Repository);
            else
                Assert.Empty(snapshot.Exclusions);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OfflineDoesNotAccessNetwork()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected network")));
        var catalog = new HuggingFaceTranslationModelCatalog(client, Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString(), "catalog.json"));
        var snapshot = await catalog.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(snapshot.Models);
    }

    [Fact]
    public async Task EnumeratesBothPublishersAndFollowsPagination()
    {
        string directory = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString());
        var urls = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            urls.Add(url);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            if (urls.Count == 1) response.Headers.Add("Link", "<https://huggingface.co/api/models?cursor=next>; rel=\"next\"");
            return response;
        }));
        try
        {
            var catalog = new HuggingFaceTranslationModelCatalog(client, Path.Join(directory, "catalog.json"));
            var snapshot = await catalog.RefreshAsync(false, TestContext.Current.CancellationToken);
            Assert.Equal(3, urls.Count);
            Assert.Contains("author=onnx-community", urls[0]);
            Assert.Contains("cursor=next", urls[1]);
            Assert.Contains("author=Xenova", urls[2]);
            Assert.NotEqual(DateTimeOffset.MinValue, snapshot.FetchedAtUtc);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailurePreservesLastCompleteSnapshot(bool timeout)
    {
        string directory = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString());
        bool fail = false;
        using var client = new HttpClient(new Handler(_ =>
        {
            if (fail && timeout) throw new TaskCanceledException("Metadata request timed out.");
            return fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        try
        {
            var catalog = new HuggingFaceTranslationModelCatalog(client, Path.Join(directory, "catalog.json"));
            var first = await catalog.RefreshAsync(false, TestContext.Current.CancellationToken);
            fail = true;
            Assert.Same(first, await catalog.RefreshAsync(false, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
