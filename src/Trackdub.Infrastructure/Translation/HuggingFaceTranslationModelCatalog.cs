using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trackdub.Contracts.Translation;

namespace Trackdub.Infrastructure.Translation;

/// <summary>Discovers metadata only. Weight downloads remain owned by model provisioning.</summary>
public sealed class HuggingFaceTranslationModelCatalog(HttpClient httpClient, string cachePath) : ITranslationModelCatalog
{
    private const string Policy = "opus-mt-publishers-v1";
    private static readonly string[] Publishers = ["onnx-community", "Xenova"];
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private TranslationModelCatalogSnapshot snapshot = LoadCache(cachePath);

    public TranslationModelCatalogSnapshot Snapshot => Volatile.Read(ref snapshot);

    public bool IsStale => DateTimeOffset.UtcNow - Snapshot.FetchedAtUtc > TimeSpan.FromHours(24);

    public async Task<TranslationModelCatalogSnapshot> RefreshAsync(bool offline, CancellationToken cancellationToken)
    {
        if (offline) return Snapshot;
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var models = ImmutableArray.CreateBuilder<TranslationModelDescriptor>();
            var exclusions = ImmutableArray.CreateBuilder<TranslationModelExclusion>();
            foreach (string publisher in Publishers)
            {
                Uri? next = new($"https://huggingface.co/api/models?author={publisher}&search=opus-mt-&limit=100&full=true");
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (next is not null)
                {
                    if (!visited.Add(next.AbsoluteUri)) throw new InvalidDataException("Catalog pagination cycle.");
                    using HttpResponseMessage response = await httpClient.GetAsync(next, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using JsonDocument page = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (page.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid catalog page.");
                    foreach (string repository in page.RootElement.EnumerateArray()
                        .Select(item => GetString(item, "id"))
                        .Where(repository => repository is not null && repository.StartsWith(publisher + "/opus-mt-", StringComparison.Ordinal))
                        .Select(repository => repository!))
                    {
                        try
                        {
                            models.AddRange(await ResolveAsync(repository, cancellationToken).ConfigureAwait(false));
                        }
                        catch (InvalidDataException ex)
                        {
                            exclusions.Add(new(repository, ex.Message));
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
                        {
                            // A well-formed but wrongly shaped per-model payload the explicit checks missed must
                            // exclude only that candidate. Unparseable JSON (JsonException) still aborts the refresh
                            // so a truncated response never replaces the complete snapshot.
                            exclusions.Add(new(repository, "Malformed repository metadata."));
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            // A listed repository can disappear or omit a required export file.
                            // Exclude that candidate without discarding unrelated valid pairs.
                            exclusions.Add(new(repository, "Required repository metadata or export file is missing."));
                        }
                    }
                    next = GetNext(response);
                }
            }
            var updated = new TranslationModelCatalogSnapshot(DateTimeOffset.UtcNow, Policy,
                models.DistinctBy(model => (model.Repository, model.SourceLanguage, model.TargetLanguage)).ToImmutableArray(), exclusions.ToImmutable());
            string? directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = cachePath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, cachePath, overwrite: true);
            Volatile.Write(ref snapshot, updated);
            return updated;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Snapshot;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            // Never replace a complete snapshot with a partial network refresh.
            return Snapshot;
        }
        finally { refreshGate.Release(); }
    }

    private async Task<ImmutableArray<TranslationModelDescriptor>> ResolveAsync(string repository, CancellationToken ct)
    {
        if (!IsSafeRepositoryId(repository))
            throw new InvalidDataException("Invalid repository identifier.");
        using JsonDocument detail = await GetJsonAsync($"https://huggingface.co/api/models/{repository}?blobs=true", ct).ConfigureAwait(false);
        JsonElement root = detail.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid model metadata.");
        string revision = GetString(root, "sha") ?? throw new InvalidDataException("Missing pinned revision.");
        if (revision.Length != 40 || !revision.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid pinned revision.");
        string pair = repository[(repository.IndexOf("/opus-mt-", StringComparison.Ordinal) + 9)..];
        string[] segments = pair.Split('-');
        // Multi-part names such as opus-mt-tc-big-de-en carry the direction in their last two segments.
        if (segments.Length < 2) throw new InvalidDataException("Unresolved language direction.");
        string[] languages = [segments[^2], segments[^1]];
        using JsonDocument config = await GetJsonAsync($"https://huggingface.co/{repository}/resolve/{revision}/config.json", ct).ConfigureAwait(false);
        if (GetString(config.RootElement, "model_type") != "marian") throw new InvalidDataException("Unsupported model architecture.");
        var (license, evidence, upstreamReadme) = await ResolveLicenseAsync(repository, revision, root, config.RootElement, ct).ConfigureAwait(false);
        if (license is not ("apache-2.0" or "mit" or "cc-by-4.0")) throw new InvalidDataException("Unknown or unsupported weight license.");
        ImmutableArray<TranslationModelFile> files = await CollectFilesAsync(repository, revision, root, ct).ConfigureAwait(false);
        string[] required = ["onnx/encoder_model.onnx", "onnx/decoder_model_merged.onnx", "config.json", "tokenizer_config.json", "vocab.json", "source.spm", "target.spm"];
        if (required.Any(path => !files.Any(file => file.Path == path))) throw new InvalidDataException("Incomplete supported Marian export.");
        string[] sources = ParseLanguages(upstreamReadme, "source languages", languages[0]);
        string[] targets = ParseLanguages(upstreamReadme, "target languages", languages[1]);
        if (sources.Length == 0 || targets.Length == 0) throw new InvalidDataException("Language groups require explicit source and target metadata.");
        return await BuildDescriptorsAsync(repository, revision, sources, targets, license, evidence, files, ct).ConfigureAwait(false);
    }

    private async Task<(string License, string Evidence, string? UpstreamReadme)> ResolveLicenseAsync(
        string repository, string revision, JsonElement root, JsonElement config, CancellationToken ct)
    {
        string license = root.TryGetProperty("cardData", out JsonElement card) ? GetString(card, "license") ?? "" : "";
        string evidence = $"https://huggingface.co/{repository}/blob/{revision}/README.md";
        string? upstream = card.ValueKind == JsonValueKind.Object ? GetString(card, "base_model") : null;
        if (upstream is null && card.ValueKind == JsonValueKind.Object && card.TryGetProperty("base_model", out JsonElement bases) && bases.ValueKind == JsonValueKind.Array)
        {
            string[] candidates = bases.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray();
            if (candidates.Length == 1) upstream = candidates[0];
        }
        upstream ??= GetString(config, "_name_or_path");
        string? upstreamReadme = null;
        if (upstream is not null && IsSafeRepositoryId(upstream) && upstream.StartsWith("Helsinki-NLP/opus-mt-", StringComparison.Ordinal) && !upstream.Contains("..", StringComparison.Ordinal))
        {
            using JsonDocument upstreamDetail = await GetJsonAsync($"https://huggingface.co/api/models/{upstream}", ct).ConfigureAwait(false);
            string upstreamRevision = GetString(upstreamDetail.RootElement, "sha") ?? throw new InvalidDataException("Missing upstream revision.");
            if (upstreamRevision.Length != 40 || !upstreamRevision.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid upstream revision.");
            string upstreamLicense = upstreamDetail.RootElement.TryGetProperty("cardData", out JsonElement upstreamCard) ? GetString(upstreamCard, "license") ?? "" : "";
            if (license.Length > 0 && upstreamLicense.Length > 0 && license != upstreamLicense) throw new InvalidDataException("Conflicting export and upstream licenses.");
            if (license.Length == 0)
            {
                license = upstreamLicense;
                evidence = $"https://huggingface.co/{upstream}/blob/{upstreamRevision}/README.md";
            }
            upstreamReadme = System.Text.Encoding.UTF8.GetString(await ReadMetadataAsync($"https://huggingface.co/{upstream}/resolve/{upstreamRevision}/README.md", ct).ConfigureAwait(false));
        }
        return (license, evidence, upstreamReadme);
    }

    private async Task<ImmutableArray<TranslationModelFile>> CollectFilesAsync(string repository, string revision, JsonElement root, CancellationToken ct)
    {
        var files = ImmutableArray.CreateBuilder<TranslationModelFile>();
        if (!root.TryGetProperty("siblings", out JsonElement siblings) || siblings.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Missing model file list.");
        foreach (JsonElement file in siblings.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid model file entry.");
            string path = GetString(file, "rfilename") ?? "";
            if (!IsRequiredFile(path)) continue;
            if (path.Split('/').Any(segment => segment is ".." or "." or "") || path.Contains('\\') || path.Contains(':')) throw new InvalidDataException("Unsafe artifact path.");
            string? hash = file.TryGetProperty("lfs", out JsonElement lfs) ? GetString(lfs, "sha256") : null;
            long size = 0;
            if (file.TryGetProperty("size", out JsonElement sizeElement) &&
                (sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out size) || size < 0))
                throw new InvalidDataException($"Invalid file size: {path}");
            if (hash is null && !path.EndsWith(".onnx", StringComparison.Ordinal))
            {
                byte[] metadata = await ReadMetadataAsync($"https://huggingface.co/{repository}/resolve/{revision}/{path}", ct).ConfigureAwait(false);
                size = metadata.LongLength;
                hash = Convert.ToHexStringLower(SHA256.HashData(metadata));
            }
            if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException($"Missing SHA-256: {path}");
            files.Add(new(path, size, hash));
        }
        return files.ToImmutable();
    }

    private async Task<ImmutableArray<TranslationModelDescriptor>> BuildDescriptorsAsync(
        string repository, string revision, string[] sources, string[] targets, string license, string evidence,
        ImmutableArray<TranslationModelFile> files, CancellationToken ct)
    {
        bool multilingual = sources.Length > 1 || targets.Length > 1;
        var descriptors = ImmutableArray.CreateBuilder<TranslationModelDescriptor>();
        using JsonDocument? targetVocabulary = targets.Length > 1
            ? await GetJsonAsync($"https://huggingface.co/{repository}/resolve/{revision}/vocab.json", ct).ConfigureAwait(false)
            : null;
        if (targetVocabulary is not null && targetVocabulary.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid target vocabulary.");
        foreach (string source in sources)
            foreach (string target in targets)
            {
                string? prefix = targets.Length > 1 ? $">>{target}<<" : null;
                if (prefix is not null && !targetVocabulary!.RootElement.TryGetProperty(prefix, out _)) continue;
                descriptors.Add(new($"hf:{repository}", repository, revision, NormalizeLanguage(source), NormalizeLanguage(target), prefix, multilingual, license, evidence, files));
            }
        if (descriptors.Count == 0) throw new InvalidDataException("No verified target language prefixes.");
        return descriptors.ToImmutable();
    }

    private static string[] ParseLanguages(string? readme, string label, string fallback)
    {
        string? line = readme?.Split('\n').FirstOrDefault(value => value.TrimStart(' ', '*', '-').StartsWith(label + ":", StringComparison.OrdinalIgnoreCase));
        if (line is not null)
        {
            string[] parsed = line[(line.IndexOf(':') + 1)..].Split([' ', ',', '\r', '`'], StringSplitOptions.RemoveEmptyEntries);
            if (parsed.Length > 0 && parsed.All(IsLanguage))
                return parsed.OrderBy(language => language.Contains('_') ? 1 : 0).ToArray();
        }
        return IsLanguage(fallback) ? [fallback] : [];
    }

    private static bool IsLanguage(string value)
    {
        string[] parts = value.Split('_');
        return parts.Length is 1 or 2 && parts[0].Length is 2 or 3 && parts[0].All(char.IsAsciiLetterLower) &&
            parts[0] is not ("mul" or "zle" or "gmq" or "gem" or "ine" or "roa" or "sla") &&
            (parts.Length == 1 || (parts[1].Length == 2 && parts[1].All(char.IsAsciiLetter)));
    }

    private static readonly IReadOnlyDictionary<string, string> LanguageAliases = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(culture => culture.TwoLetterISOLanguageName.Length == 2 && culture.ThreeLetterISOLanguageName.Length == 3)
        .GroupBy(culture => culture.ThreeLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().TwoLetterISOLanguageName, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeLanguage(string language)
    {
        string primary = language.Split('_')[0];
        return LanguageAliases.TryGetValue(primary, out string? canonical) ? canonical : primary;
    }

    private static bool IsRequiredFile(string path) => path is "onnx/encoder_model.onnx" or "onnx/decoder_model_merged.onnx" or "config.json" or "tokenizer_config.json" or "vocab.json" or "source.spm" or "target.spm";
    private async Task<byte[]> ReadMetadataAsync(string url, CancellationToken ct)
    {
        const int maximumBytes = 16 * 1024 * 1024;
        using HttpResponseMessage response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Metadata exceeds size limit.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("Metadata exceeds size limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
    }
    private static string? GetString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static Uri? GetNext(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? values)) return null;
        Match link = Regex.Match(string.Join(",", values), "<([^>]+)>\\s*;\\s*rel=\"next\"");
        if (!link.Success) return null;
        if (!Uri.TryCreate(link.Groups[1].Value, UriKind.Absolute, out Uri? next) || next.Scheme != "https" || next.Host != "huggingface.co" || !next.AbsolutePath.Equals("/api/models", StringComparison.Ordinal)) throw new InvalidDataException("Unsafe catalog pagination URL.");
        return next;
    }
    private static bool IsSafeRepositoryId(string value) =>
        value.Length > 0 && value.Count(character => character == '/') == 1 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.');
    private static TranslationModelCatalogSnapshot LoadCache(string path)
    {
        try
        {
            TranslationModelCatalogSnapshot? cached = File.Exists(path) ? JsonSerializer.Deserialize<TranslationModelCatalogSnapshot>(File.ReadAllText(path)) : null;
            if (cached is null || cached.PolicyVersion != Policy || cached.Models.IsDefault || cached.Exclusions.IsDefault) return TranslationModelCatalogSnapshot.Empty;
            if (cached.Models.Any(model => model.Files.IsDefaultOrEmpty ||
                !IsSafeRepositoryId(model.Repository) ||
                !Publishers.Any(publisher => model.Repository.StartsWith(publisher + "/opus-mt-", StringComparison.Ordinal)) ||
                model.Revision.Length != 40 || !model.Revision.All(Uri.IsHexDigit) ||
                model.License is not ("apache-2.0" or "mit" or "cc-by-4.0") ||
                model.Files.Any(file => !IsRequiredFile(file.Path) || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)))) return TranslationModelCatalogSnapshot.Empty;
            return cached;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return TranslationModelCatalogSnapshot.Empty; }
    }
}
