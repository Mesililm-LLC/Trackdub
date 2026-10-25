using System.Collections.Concurrent;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Thread-safe, keyed cache for ONNX model sidecar data such as tokenizers and voice catalogs.
/// The value factory is invoked at most once per distinct cache key regardless of
/// concurrent callers.  Entries persist until explicitly removed via <see cref="Remove(string)"/> or
/// <see cref="Clear()"/>.
/// </summary>
/// <typeparam name="T">Type of the cached sidecar value.</typeparam>
/// <remarks>
/// <para>Eviction policy: this cache is unbounded by design because sidecar artefacts are small
/// (tokenizer vocabs are tens of KB; voice-catalog index entries are byte-scale metadata).</para>
///
/// <para><strong>Fault handling:</strong>
/// If the value factory throws, the faulted entry is automatically removed from the cache so
/// subsequent callers can retry (potentially with a repaired environment or a different factory).
/// A successful result is cached indefinitely.</para>
///
/// <para><strong>Invalidation:</strong>
/// Entries are keyed by the model-root path string only — no file-content hash is checked on
/// read.  This means:
/// <list type="bullet">
///   <item><description>If the model-root directory is replaced in-place while the process is
///     running (e.g. hot-swap during development), the stale entry will continue to be served
///     until it is explicitly removed via <see cref="Remove(string)"/> or <see cref="Clear()"/>.</description></item>
///   <item><description>On a normal restart, the cache is fresh (static initialisation).</description></item>
/// </list>
/// Content-hash invalidation on every access is out of scope for this PR; add it in a
/// follow-up if live model hot-swap becomes a production requirement.</para>
/// </remarks>
internal sealed class SidecarCache<T>
{
    // ExecutionAndPublication guarantees the factory runs exactly once per key even when
    // multiple threads race; only one thread executes the factory, others wait for the result.
    // On fault the entry is evicted (see GetOrAdd) so callers can retry.
    private readonly ConcurrentDictionary<string, Lazy<T>> store =
        new(CreatePathComparer());

    private static StringComparer CreatePathComparer() =>
        // Case-insensitive only on Windows, where model paths are typically case-insensitive.
        // macOS APFS can be case-sensitive or case-insensitive depending on volume format,
        // so we default to case-sensitive (Ordinal) to avoid conflating distinct paths
        // on case-sensitive APFS or Linux ext4 volumes.
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or creates and caches it using
    /// <paramref name="valueFactory"/> if it has not been cached yet.
    /// The factory is invoked at most once per key across all concurrent callers.
    /// If the factory throws, the faulted entry is evicted so the next call to
    /// <see cref="GetOrAdd"/> can retry (the exception is still propagated to the current caller).
    /// </summary>
    public T GetOrAdd(string key, Func<T> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(valueFactory);

        Lazy<T> lazy = store.GetOrAdd(
            key,
            _ => new Lazy<T>(valueFactory, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazy.Value;
        }
        catch
        {
            // Factory faulted. With ExecutionAndPublication the exception is cached inside
            // lazy, so every future access to that Lazy<T> would rethrow the cached exception.
            // Evict the stale entry so subsequent GetOrAdd calls can create a fresh Lazy<T>
            // and retry.  The value-comparison overload prevents accidentally removing an entry
            // that a concurrent successful caller may have already replaced.
            store.TryRemove(new KeyValuePair<string, Lazy<T>>(key, lazy));
            throw;
        }
    }

    /// <summary>Removes the cached entry for <paramref name="key"/> from both the sync and async stores.</summary>
    /// <returns><see langword="true"/> if an entry was found and removed; otherwise <see langword="false"/>.</returns>
    public bool Remove(string key) => TryRemove(key, out _);

    /// <summary>
    /// Removes the cached entry for <paramref name="key"/> from both the sync and async stores,
    /// and returns the removed value if it had already been created successfully.
    /// </summary>
    /// <remarks>
    /// This method does not force evaluation of unevaluated <see cref="Lazy{T}"/> instances
    /// during eviction. If the entry exists but its value has not been created yet, the entry is still
    /// removed and <paramref name="value"/> is set to <see langword="default"/> (which may be
    /// <see langword="null"/> for reference types). Callers should null-check <paramref name="value"/>
    /// even when the return value is <see langword="true"/>.
    /// </remarks>
    /// <returns><see langword="true"/> if an entry was found and removed in either store; otherwise <see langword="false"/>.</returns>
    public bool TryRemove(string key, out T? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        bool removedFromSync = store.TryRemove(key, out Lazy<T>? syncLazy);
        bool removedFromAsync = storeAsync.TryRemove(key, out Lazy<Task<T>>? asyncLazy);

        value = default;

        // Prefer the sync value if it was already created; otherwise try async.
        // Only read the async task's result if it has already completed successfully —
        // IsValueCreated becomes true as soon as the Task object is allocated, not when
        // the load finishes. Blocking here on an in-flight load would deadlock callers
        // and propagate load exceptions from an eviction path.
        if (removedFromSync && syncLazy!.IsValueCreated)
        {
            value = syncLazy.Value;
        }
        else if (removedFromAsync && asyncLazy!.IsValueCreated
                 && asyncLazy.Value.IsCompletedSuccessfully)
        {
            value = asyncLazy.Value.GetAwaiter().GetResult();
        }

        return removedFromSync || removedFromAsync;
    }

    /// <summary>Removes all cached entries.</summary>
    public void Clear()
    {
        store.Clear();
        storeAsync.Clear();
    }

    /// <summary>
    /// Removes all cached entries and invokes <paramref name="onEvict"/> for each value that had
    /// already been created successfully.
    /// </summary>
    /// <remarks>
    /// <para>This method does not force evaluation of unevaluated <see cref="Lazy{T}"/> instances
    /// during eviction.</para>
    /// <para>For async entries, <paramref name="onEvict"/> is invoked only when the underlying
    /// task has already completed successfully at snapshot time.</para>
    /// <para><strong>Concurrent-writer behaviour:</strong> <paramref name="onEvict"/> is called
    /// only for entries present in the snapshot taken at the start of this call.  Entries added
    /// by concurrent writers between the snapshot and <see cref="ConcurrentDictionary{TKey,TValue}.Clear"/>
    /// are removed but <paramref name="onEvict"/> is <em>not</em> invoked for them.
    /// Callers that require deterministic disposal of all values under concurrent activity must
    /// coordinate externally (e.g. quiesce writers before calling the eviction-callback overload).</para>
    /// </remarks>
    public void Clear(Action<T> onEvict)
    {
        ArgumentNullException.ThrowIfNull(onEvict);

        // Snapshot both stores, then atomically clear them, then invoke the callback for
        // values that were successfully created.  Using Clear() after the snapshot ensures
        // every present entry is removed, even if a key was concurrently re-added between
        // enumeration and removal (e.g. after a faulted Lazy<T> was evicted and the factory
        // was retried with a new entry).
        // Note: entries added between ToArray() and Clear() are removed by Clear() but
        // onEvict is not invoked for them (see remarks).
        KeyValuePair<string, Lazy<T>>[] syncSnapshot = store.ToArray();
        KeyValuePair<string, Lazy<Task<T>>>[] asyncSnapshot = storeAsync.ToArray();
        store.Clear();
        storeAsync.Clear();

        foreach (KeyValuePair<string, Lazy<T>> entry in syncSnapshot)
        {
            if (entry.Value.IsValueCreated)
            {
                onEvict(entry.Value.Value);
            }
        }

        foreach (KeyValuePair<string, Lazy<Task<T>>> entry in asyncSnapshot)
        {
            if (entry.Value.IsValueCreated
                && entry.Value.Value.IsCompletedSuccessfully)
            {
                onEvict(entry.Value.Value.GetAwaiter().GetResult());
            }
        }
    }
    /// <summary>
    /// Async variant of <see cref="GetOrAdd"/> for factories that perform I/O.
    /// Returns the cached value for <paramref name="key"/>, or creates and caches it using
    /// <paramref name="valueFactory"/> if it has not been cached yet.
    /// The factory is invoked at most once per key across all concurrent callers.
    /// If the factory throws, the faulted entry is evicted so the next call to
    /// <see cref="GetOrAddAsync"/> can retry.
    /// </summary>
    public async Task<T> GetOrAddAsync(string key, Func<string, Task<T>> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(valueFactory);

        Lazy<Task<T>> lazy = storeAsync.GetOrAdd(
            key,
            k => new Lazy<Task<T>>(() => valueFactory(k), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            storeAsync.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, lazy));
            throw;
        }
    }

    /// <summary>Removes all cached entries whose key is rooted at <paramref name="pathPrefix"/>.</summary>
    /// <returns>
    /// The number of distinct keys removed. A key present in both the sync and async stores counts
    /// once, because <see cref="TryRemove"/> clears both.
    /// </returns>
    public int RemoveByPathPrefix(string pathPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPrefix);
        string prefix = Path.GetFullPath(pathPrefix);
        if (!prefix.EndsWith(Path.DirectorySeparatorChar))
        {
            prefix += Path.DirectorySeparatorChar;
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Snapshot both key sets, then visit each distinct key once. TryRemove clears a key from
        // both stores, so a key present in both snapshots must not be processed twice: a concurrent
        // reload of that key between passes would otherwise be removed by the second pass and
        // counted again, breaking the distinct-key contract and discarding a fresh entry.
        var keys = new HashSet<string>(CreatePathComparer());
        foreach (string key in store.Keys.Where(k => k.StartsWith(prefix, comparison)))
        {
            keys.Add(key);
        }

        foreach (string key in storeAsync.Keys.Where(k => k.StartsWith(prefix, comparison)))
        {
            keys.Add(key);
        }

        int removed = 0;
        foreach (string key in keys)
        {
            if (TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Number of entries currently cached.</summary>
    public int Count => store.Count + storeAsync.Count;

    // Separate dictionary for async entries to avoid Lazy<T> / Lazy<Task<T>> type mismatch.
    // The sync and async caches are independent — each entry type uses its own dictionary.
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> storeAsync =
        new(CreatePathComparer());
}
