using System.Text;
using System.Text.Json;
using Trackdub.Domain;
using Trackdub.Infrastructure;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class LocalModelCacheRecordStore(TrackdubStoragePaths storagePaths)
{
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private int revision;

    internal int Revision => Volatile.Read(ref revision);

    /// <summary>
    /// Cross-process identity of the persisted index file (mtime + length + content hash),
    /// or null when the file is absent or unreadable. The lookup keys its in-memory cache on
    /// this in addition to <see cref="Revision"/> so writes from another process (CLI installs,
    /// a second instance) invalidate this process's snapshot; <see cref="Revision"/> alone is
    /// process-local. The content hash also catches same-length replacements that preserve
    /// the modification time, which mtime + length cannot distinguish.
    /// </summary>
    internal (long LastWriteTicks, long Length, string ContentHash)? GetIndexIdentity()
    {
        try
        {
            var info = new FileInfo(storagePaths.ModelCacheIndexPath);
            if (!info.Exists)
            {
                return null;
            }

            // Share read/write/delete so this probe never blocks a concurrent
            // atomic replace (File.Move overwrite) of the index on Windows.
            using var stream = new FileStream(
                info.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            string contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            return (info.LastWriteTimeUtc.Ticks, info.Length, contentHash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically reads the current cache index, applies <paramref name="mutator"/>, and persists the result.
    /// This is the only thread-safe way to update the index: concurrent calls are serialized through an
    /// internal lock so read-modify-write sequences cannot lose updates. All production mutations must go
    /// through this method.
    /// </summary>
    public async Task MutateAsync(
        Func<IReadOnlyList<LocalModelCacheRecord>, IReadOnlyList<LocalModelCacheRecord>> mutator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutator);

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<LocalModelCacheRecord> current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            await SaveAsync(mutator(current), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    /// <summary>
    /// Reads the cache index. Safe for concurrent readers; does not acquire the mutation lock.
    /// Callers that modify and persist must use <see cref="MutateAsync"/> instead.
    /// </summary>
    /// <remarks>
    /// The index is small and always read whole, so it is read into a buffer synchronously and
    /// deserialized from memory, matching the synchronous reads in <see cref="FileSmokeVerdictStore"/>
    /// and <see cref="TrackdubStoragePathResolver"/>. Measured on a two-record index, this removes
    /// ~9 ms of the one-time first read and roughly halves the per-read cost after it (0.30 ms →
    /// 0.16 ms) that the async reader spent on its own machinery; the dominant cost is still
    /// building the serializer metadata for the record graph, which this does not change.
    /// <para>
    /// A leading UTF-8 byte order mark is skipped, so an index written by an editor or script that
    /// emits one (Notepad, PowerShell <c>-Encoding utf8</c>) loads exactly as it did through the
    /// stream-based read this replaced. The store never writes a mark itself.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<LocalModelCacheRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Load(cancellationToken));
    }

    internal IReadOnlyList<LocalModelCacheRecord> Load(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(storagePaths.ModelCacheIndexPath))
        {
            return [];
        }

        byte[] payload = File.ReadAllBytes(storagePaths.ModelCacheIndexPath);
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<byte> json = payload;
        ReadOnlySpan<byte> preamble = Encoding.UTF8.GetPreamble();
        if (json.StartsWith(preamble))
        {
            json = json[preamble.Length..];
        }

        LocalModelCacheRecord[]? records = JsonSerializer.Deserialize(
            json,
            LocalModelCacheSerializationContext.Default.LocalModelCacheRecordArray);

        return NormalizeRecords(records ?? []);
    }

    /// <summary>
    /// Persists the cache index via temp-file write and replace. Not thread-safe by itself; production code
    /// must route mutations through <see cref="MutateAsync"/>. Internal so tests can seed initial state.
    /// </summary>
    internal async Task SaveAsync(
        IReadOnlyList<LocalModelCacheRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        Directory.CreateDirectory(storagePaths.ModelCacheDirectory);
        string indexPath = storagePaths.ModelCacheIndexPath;
        string tempPath = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             options: FileOptions.Asynchronous))
            {
                // The generated metadata is bound to LocalModelCacheRecord[], so materialize
                // IReadOnlyList<T> implementations (e.g. single-element wrappers) before serializing.
                LocalModelCacheRecord[] materialized = [.. NormalizeRecords(records)];
                await JsonSerializer.SerializeAsync(stream, materialized, LocalModelCacheSerializationContext.Default.LocalModelCacheRecordArray, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, indexPath, overwrite: true);
            Interlocked.Increment(ref revision);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public static bool RootsEqual(string left, string right)
    {
        if (TryNormalizeRoot(left, out string? normalizedLeft) &&
            TryNormalizeRoot(right, out string? normalizedRight))
        {
            return string.Equals(normalizedLeft, normalizedRight, RootPathComparison);
        }

        // Never throw from equality: fall back to a raw trimmed comparison when
        // either path is missing or cannot be normalized.
        return string.Equals(left?.Trim(), right?.Trim(), RootPathComparison);
    }

    public static IReadOnlyList<LocalModelVariantRecord> CompatibleVariants(
        LocalModelCacheRecord record, IEnumerable<LocalModelVariantRecord> variants) =>
        variants.Where(variant =>
                string.Equals(variant.SourceModelRevision, record.Revision, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(variant.SourceModelSha256) ||
                 string.Equals(variant.SourceModelSha256, record.Sha256, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(variant => variant.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(variant => variant.CreatedAtUtc)
                .ThenByDescending(variant => variant.IntegrityFailed).First())
            .ToArray();

    private static IReadOnlyList<LocalModelCacheRecord> NormalizeRecords(IReadOnlyList<LocalModelCacheRecord> records) =>
        records.GroupBy(record => record.ModelId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(model => model.GroupBy(record => NormalizeRootKey(record.RootPath), RootPathComparer))
            .Select(group =>
            {
                LocalModelCacheRecord newest = group.OrderByDescending(record => record.CachedAtUtc)
                    .ThenByDescending(record => record.IntegrityFailed).First();
                return group.Count() == 1 ? newest : newest with
                {
                    Variants = CompatibleVariants(newest, group.SelectMany(record => record.Variants))
                };
            }).ToArray();

    private static StringComparer RootPathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison RootPathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string NormalizeRootKey(string rootPath) =>
        TryNormalizeRoot(rootPath, out string? normalized)
            ? normalized
            : (rootPath ?? string.Empty).Trim();

    private static bool TryNormalizeRoot(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
