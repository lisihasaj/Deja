using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deja;

/// <summary>
/// The shared query cache: a registry of <see cref="CacheEntry{T}"/> keyed by
/// <see cref="QueryKey"/>, plus invalidation, manual reads/writes, per-prefix defaults, and
/// eviction of idle entries. Register it with <c>AddDeja()</c> — Scoped, which is one cache per
/// browser tab on WebAssembly and one per user circuit on Blazor Server (a singleton on Server
/// would share one user's cached API responses with every other user).
/// </summary>
/// <remarks>
/// Queries opt in by setting a <see cref="QueryParameters{T}.QueryKey"/>; queries without a key
/// never touch the cache. Components inheriting <see cref="DejaComponentBase"/> get the client
/// handed to their queries automatically; outside a component, pass it via
/// <see cref="QueryParameters{T}.Client"/> or the <see cref="Query{T}"/> constructor.
/// </remarks>
public sealed class DejaClient : IDisposable
{
    // QueryKey.Hash -> entry. The hash is precomputed once per key, so a lookup is a single
    // string-keyed dictionary read.
    private readonly ConcurrentDictionary<string, ICacheEntry> _entries = new(StringComparer.Ordinal);

    private readonly object _defaultsLock = new();
    private readonly List<(QueryKey Prefix, QueryDefaults Defaults)> _prefixDefaults = [];
    private readonly DejaOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    /// <summary>Creates a client, optionally with non-default <see cref="DejaOptions"/>.</summary>
    public DejaClient(DejaOptions? options = null)
    {
        _options = options ?? new DejaOptions();
        _time = _options.TimeProvider;
        _ = RunEvictionLoopAsync(_disposeCts.Token);
    }

    /// <summary>The clock queries use for staleness; taken from <see cref="DejaOptions.TimeProvider"/>.</summary>
    internal TimeProvider Time => _time;

    /// <summary>Number of live cache entries; test observability.</summary>
    internal int EntryCount => _entries.Count;

    /// <summary>
    /// The cached data under <paramref name="key"/>, or <c>default</c> when the key is absent,
    /// has no data yet, or was stored with a different <typeparamref name="T"/>.
    /// </summary>
    public T? GetData<T>(QueryKey key) => TryGetData<T>(key, out var data) ? data : default;

    /// <summary>
    /// Reads the cached data under <paramref name="key"/>. Returns <see langword="false"/> when
    /// the key is absent or has no data. A key stored with a different <typeparamref name="T"/>
    /// also returns <see langword="false"/> rather than throwing — it almost always means two
    /// call sites disagree about the key's shape, and is reported as a debug-level warning.
    /// </summary>
    public bool TryGetData<T>(QueryKey key, out T? data)
    {
        ArgumentNullException.ThrowIfNull(key);

        data = default;

        if (!_entries.TryGetValue(key.Hash, out var entry))
        {
            return false;
        }

        if (entry is not CacheEntry<T> typed)
        {
            Debug.WriteLine(
                $"[Deja] Cache entry {key} holds {entry.DataType} but {typeof(T)} was requested; " +
                "returning default. Two call sites most likely disagree about this key's shape.");
            return false;
        }

        typed.Touch();
        return typed.TryGetData(out data);
    }

    /// <summary>A read-only snapshot of the entry under <paramref name="key"/>, or <see langword="null"/> when absent.</summary>
    public CacheEntryState? GetState(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _entries.TryGetValue(key.Hash, out var entry) ? entry.GetState() : null;
    }

    /// <summary>
    /// Writes <paramref name="data"/> under <paramref name="key"/> (creating the entry when
    /// absent) and notifies every subscribed query. The manual alternative to invalidation when
    /// the new value is already known — e.g. appending a mutation result to a cached list.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key holds data of a different type.</exception>
    public void SetData<T>(QueryKey key, T data)
    {
        ArgumentNullException.ThrowIfNull(key);

        GetOrCreateEntry<T>(key).SetData(data);
    }

    /// <summary>
    /// Transforms the cached value under <paramref name="key"/>: <paramref name="updater"/>
    /// receives the current data (or <c>default</c> when none) and returns the new value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key holds data of a different type.</exception>
    public void SetData<T>(QueryKey key, Func<T?, T> updater)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(updater);

        var entry = GetOrCreateEntry<T>(key);
        entry.TryGetData(out var current);
        entry.SetData(updater(current));
    }

    /// <summary>
    /// Marks every entry matching <paramref name="key"/> stale — by prefix unless
    /// <see cref="InvalidateOptions.Exact"/> — and refetches per
    /// <see cref="InvalidateOptions.RefetchType"/>: by default entries with live subscribers
    /// refetch in the background (existing data stays on screen, no loading flash), while
    /// subscriber-less entries stay marked and refetch when a component next mounts on them.
    /// The returned task completes when the triggered refetches settle.
    /// </summary>
    public Task InvalidateAsync(QueryKey key, InvalidateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        options ??= new InvalidateOptions();
        return InvalidateCoreAsync(Match(key, options.Exact, options.Predicate), options.RefetchType);
    }

    /// <summary>Invalidates every entry in the cache; active entries refetch in the background.</summary>
    public Task InvalidateAllAsync()
        => InvalidateCoreAsync([.. _entries.Values], RefetchType.Active);

    /// <summary>
    /// Refetches every entry matching <paramref name="key"/> that has a fetch function,
    /// regardless of staleness. Failures are recorded on the entries, not thrown.
    /// </summary>
    public Task RefetchAsync(QueryKey key, QueryFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        var refetches = Match(key, filter?.Exact ?? false, filter?.Predicate)
            .Where(static entry => entry.CanRefetch)
            .Select(static entry => entry.RefetchAsync());

        return Task.WhenAll(refetches);
    }

    /// <summary>
    /// Removes every entry matching <paramref name="key"/>, cancelling in-flight fetches.
    /// Subscribed queries keep rendering their last data; their next <c>Execute</c> starts from
    /// an empty entry.
    /// </summary>
    public void Remove(QueryKey key, QueryFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        foreach (var entry in Match(key, filter?.Exact ?? false, filter?.Predicate))
        {
            RemoveEntry(entry);
        }
    }

    /// <summary>Removes every entry, cancelling in-flight fetches.</summary>
    public void Clear()
    {
        foreach (var pair in _entries)
        {
            RemoveEntry(pair.Value);
        }
    }

    // Removes exactly this entry instance (not a same-key replacement) and cancels its fetch.
    private void RemoveEntry(ICacheEntry entry)
    {
        if (_entries.TryRemove(new KeyValuePair<string, ICacheEntry>(entry.Key.Hash, entry)))
        {
            entry.Dispose();
        }
    }

    /// <summary>
    /// Registers defaults for every key starting with <paramref name="prefix"/>, replacing any
    /// previous defaults for the same prefix. Precedence, most specific wins:
    /// <see cref="QueryParameters{T}"/> value &gt; longest matching prefix &gt;
    /// <see cref="DejaOptions"/> — registration order does not matter.
    /// </summary>
    public void SetDefaults(QueryKey prefix, QueryDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(defaults);

        lock (_defaultsLock)
        {
            _prefixDefaults.RemoveAll(existing => existing.Prefix.Equals(prefix));
            _prefixDefaults.Add((prefix, defaults));

            // Longest prefix first, so lookup takes the most specific match regardless of the
            // order defaults were registered in.
            _prefixDefaults.Sort(static (a, b) => b.Prefix.SegmentCount.CompareTo(a.Prefix.SegmentCount));
        }
    }

    /// <summary>Stops the eviction timer and drops every entry; called by DI when the scope ends.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        Clear();
    }

    /// <summary>
    /// The entry for <paramref name="key"/>, created on first use. Throws when the key already
    /// holds a different <typeparamref name="T"/> — two call sites disagreeing about a key's
    /// shape is a bug that silent replacement would mask.
    /// </summary>
    internal CacheEntry<T> GetOrCreateEntry<T>(QueryKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = _entries.GetOrAdd(
            key.Hash,
            _ => new CacheEntry<T>(key, _time, _options.StructuralComparison, ResolveCacheTime(key, null)));

        if (entry is not CacheEntry<T> typed)
        {
            throw new InvalidOperationException(
                $"Cache key {key} already holds data of type {entry.DataType}, but {typeof(T)} was " +
                "requested. Two call sites disagree about this key's shape — give them distinct keys.");
        }

        typed.Touch();
        EnforceMaxEntries();
        return typed;
    }

    internal TimeSpan ResolveStaleTime(QueryKey key, TimeSpan? perQuery)
        => perQuery ?? FindDefault(key, static d => d.StaleTime) ?? _options.DefaultStaleTime;

    internal TimeSpan ResolveCacheTime(QueryKey key, TimeSpan? perQuery)
        => perQuery ?? FindDefault(key, static d => d.CacheTime) ?? _options.DefaultCacheTime;

    internal RefetchOnMount ResolveRefetchOnMount(QueryKey key, RefetchOnMount? perQuery)
        => perQuery ?? FindDefault(key, static d => d.RefetchOnMount) ?? _options.DefaultRefetchOnMount;

    // Removes entries whose idle period has elapsed. Runs on the eviction timer; internal so tests
    // can drive it deterministically with a fake TimeProvider.
    internal void Sweep()
    {
        var now = _time.GetUtcNow();

        foreach (var pair in _entries)
        {
            var entry = pair.Value;

            // A subscriber arriving between this check and the removal loses registry membership
            // but keeps a working entry; the next Execute starts a fresh one. Rare, self-healing,
            // and cheaper than locking the whole registry per sweep.
            if (entry.SubscriberCount == 0 && entry.EvictAt is { } evictAt && evictAt <= now &&
                _entries.TryRemove(pair))
            {
                entry.Dispose();
            }
        }
    }

    private static Task InvalidateCoreAsync(List<ICacheEntry> matches, RefetchType refetchType)
    {
        var refetches = new List<Task>();

        foreach (var entry in matches)
        {
            entry.Invalidate();

            var refetch = refetchType switch
            {
                RefetchType.Active => entry.SubscriberCount > 0,
                RefetchType.All => true,
                _ => false,
            };

            if (refetch && entry.CanRefetch)
            {
                refetches.Add(entry.RefetchAsync());
            }
        }

        return Task.WhenAll(refetches);
    }

    private List<ICacheEntry> Match(QueryKey key, bool exact, Func<QueryKey, bool>? predicate)
    {
        var matches = new List<ICacheEntry>();

        foreach (var entry in _entries.Values)
        {
            var keyMatches = exact ? entry.Key.Equals(key) : entry.Key.StartsWith(key);

            if (keyMatches && (predicate is null || predicate(entry.Key)))
            {
                matches.Add(entry);
            }
        }

        return matches;
    }

    private TValue? FindDefault<TValue>(QueryKey key, Func<QueryDefaults, TValue?> selector)
        where TValue : struct
    {
        lock (_defaultsLock)
        {
            foreach (var (prefix, defaults) in _prefixDefaults)
            {
                if (key.StartsWith(prefix) && selector(defaults) is { } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private void EnforceMaxEntries()
    {
        if (_options.MaxEntries is not { } max || _entries.Count <= max)
        {
            return;
        }

        // Least-recently-used zero-subscriber entries go first; subscribed entries are never
        // evicted, so the cache can exceed the cap while everything in it is on screen.
        var candidates = _entries
            .Where(static pair => pair.Value.SubscriberCount == 0)
            .OrderBy(static pair => pair.Value.LastAccessedAt)
            .ToList();

        foreach (var pair in candidates)
        {
            if (_entries.Count <= max)
            {
                break;
            }

            if (_entries.TryRemove(pair))
            {
                pair.Value.Dispose();
            }
        }
    }

    private async Task RunEvictionLoopAsync(CancellationToken token)
    {
        // On WASM this loop shares the single UI thread: the sweep must stay allocation-light and
        // must never block. It is a dictionary scan of at most MaxEntries — keep it that way.
        using var timer = new PeriodicTimer(_options.EvictionInterval, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed; the loop ends and nothing roots the client.
        }
    }
}

/// <summary>
/// Narrows which entries a key matches in <see cref="DejaClient.Remove"/> and
/// <see cref="DejaClient.RefetchAsync"/>.
/// </summary>
public sealed class QueryFilter
{
    /// <summary>
    /// When <see langword="false"/> (the default), the key matches by prefix; when
    /// <see langword="true"/>, only the exact key matches.
    /// </summary>
    public bool Exact { get; set; }

    /// <summary>Optional additional filter over the matched keys.</summary>
    public Func<QueryKey, bool>? Predicate { get; set; }
}

/// <summary>Options for <see cref="DejaClient.InvalidateAsync"/>.</summary>
public sealed class InvalidateOptions
{
    /// <summary>
    /// When <see langword="false"/> (the default), the key matches by prefix — invalidating
    /// <c>["todos"]</c> also invalidates <c>["todos", 1]</c>. When <see langword="true"/>, only
    /// the exact key matches.
    /// </summary>
    public bool Exact { get; set; }

    /// <summary>Optional additional filter over the matched keys.</summary>
    public Func<QueryKey, bool>? Predicate { get; set; }

    /// <summary>Which matched entries refetch immediately. Default: <see cref="RefetchType.Active"/>.</summary>
    public RefetchType RefetchType { get; set; } = RefetchType.Active;
}

/// <summary>Which invalidated entries refetch immediately.</summary>
public enum RefetchType
{
    /// <summary>
    /// Refetch entries that have live subscribers; entries without stay marked stale and refetch
    /// when a component next mounts on them.
    /// </summary>
    Active,

    /// <summary>Refetch every matched entry, subscribed or not.</summary>
    All,

    /// <summary>Only mark entries stale; nothing refetches until the next mount.</summary>
    None,
}

/// <summary>Registers Deja's shared query cache in the application's service collection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DejaClient"/> (Scoped) and <see cref="DejaOptions"/> (singleton),
    /// optionally configured. Components inheriting <see cref="DejaComponentBase"/> then hand the
    /// client to their queries automatically; apps that never call this keep the fully uncached
    /// behavior.
    /// </summary>
    /// <remarks>
    /// Scoped is the only correct lifetime across both hosting models. On WebAssembly the
    /// container lives as long as the app, so Scoped is effectively one cache per browser tab —
    /// what is wanted. On Blazor Server, Scoped is one cache per user circuit; a singleton would
    /// share one user's cached API responses with every other connected user, which is a data
    /// leak, not a performance win. Do not "optimize" this to a singleton.
    /// </remarks>
    public static IServiceCollection AddDeja(this IServiceCollection services, Action<DejaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new DejaOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddScoped(static provider => new DejaClient(provider.GetService<DejaOptions>()));

        return services;
    }
}
