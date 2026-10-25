using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.Tests;

/// <summary>
/// Sidecar caches are keyed by path only, so an install over an existing path must be able to
/// flush what was cached before it. These pin the invalidation seam the download path relies on.
/// </summary>
public sealed class SidecarCacheInvalidationTests
{
    [Fact]
    public void RemoveByPathPrefix_drops_every_entry_under_the_root()
    {
        var cache = new SidecarCache<string>();
        string root = Path.Join(Path.GetTempPath(), "trackdub-sidecar-root");
        string nested = Path.Join(root, "embeddings", "embed_tokens.bin");
        string sibling = Path.Join(Path.GetTempPath(), "trackdub-sidecar-other", "embed_tokens.bin");

        cache.GetOrAdd(Path.Join(root, "tokenizer.json"), () => "root");
        cache.GetOrAdd(nested, () => "nested");
        cache.GetOrAdd(sibling, () => "sibling");

        int removed = cache.RemoveByPathPrefix(root);

        Assert.Equal(2, removed);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void RemoveByPathPrefix_does_not_match_a_sibling_with_a_shared_name_prefix()
    {
        // "model" must not invalidate "model-2": the comparison is on a directory boundary.
        var cache = new SidecarCache<string>();
        string root = Path.Join(Path.GetTempPath(), "trackdub-prefix-model");
        string sibling = Path.Join(Path.GetTempPath(), "trackdub-prefix-model-2", "embed_tokens.bin");

        cache.GetOrAdd(sibling, () => "sibling");

        Assert.Equal(0, cache.RemoveByPathPrefix(root));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task RemoveByPathPrefix_also_drops_async_entries()
    {
        var cache = new SidecarCache<string>();
        string root = Path.Join(Path.GetTempPath(), "trackdub-sidecar-async");
        string key = Path.Join(root, "tokenizer.json");

        Assert.Equal(
            "loaded",
            await cache.GetOrAddAsync(key, _ => Task.FromResult("loaded")));
        Assert.Equal(1, cache.Count);

        Assert.Equal(1, cache.RemoveByPathPrefix(root));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task RemoveByPathPrefix_counts_a_shared_key_once_and_clears_both_stores()
    {
        // A key can exist in both the sync and async dictionaries. TryRemove clears both and
        // reports success once, so the count is distinct keys, not per-store entries. The
        // snapshot matters because TryRemove mutates the dictionaries the loops enumerate.
        var cache = new SidecarCache<string>();
        string root = Path.Join(Path.GetTempPath(), "trackdub-sidecar-shared");
        string key = Path.Join(root, "tokenizer.json");

        cache.GetOrAdd(key, () => "sync");
        await cache.GetOrAddAsync(key, _ => Task.FromResult("async"));
        Assert.Equal(2, cache.Count);

        Assert.Equal(1, cache.RemoveByPathPrefix(root));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void RemoveByPathPrefix_rejects_a_blank_prefix()
    {
        var cache = new SidecarCache<string>();
        Assert.Throws<ArgumentException>(() => cache.RemoveByPathPrefix(" "));
    }
}
