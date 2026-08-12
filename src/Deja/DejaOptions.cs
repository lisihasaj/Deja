namespace Deja;

/// <summary>
/// Global cache defaults, overridable per query via <see cref="QueryParameters{T}"/> and per key
/// prefix via <see cref="DejaClient.SetDefaults"/>.
/// </summary>
public sealed class DejaOptions
{
    /// <summary>
    /// How long after a successful fetch data is considered fresh. While fresh, a keyed
    /// <c>Execute</c> serves the cache and does <b>not</b> refetch. The default,
    /// <see cref="TimeSpan.Zero"/>, means cached data renders instantly but is always revalidated
    /// in the background on the next mount — "cached" does not mean "no request" unless this is
    /// raised.
    /// </summary>
    public TimeSpan DefaultStaleTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// How long an entry with zero subscribers survives before eviction. A component remounting
    /// within this window gets its data instantly. Default: 5 minutes.
    /// </summary>
    public TimeSpan DefaultCacheTime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether a keyed <c>Execute</c> that finds cached data refetches in the background.
    /// Default: <see cref="RefetchOnMount.IfStale"/>.
    /// </summary>
    public RefetchOnMount DefaultRefetchOnMount { get; set; } = RefetchOnMount.IfStale;

    /// <summary>
    /// Optional hard cap on cache entries. When exceeded, least-recently-used entries with no
    /// subscribers are evicted first. <see langword="null"/> (the default) means no cap.
    /// </summary>
    public int? MaxEntries { get; set; }

    /// <summary>How often the eviction sweep runs. Default: 1 minute.</summary>
    public TimeSpan EvictionInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// When <see langword="true"/>, an entry that refetches to a value equal to the current one
    /// (per <see cref="EqualityComparer{T}.Default"/>) keeps its old reference and skips notifying
    /// subscribers, so components do not re-render for identical data. Default:
    /// <see langword="false"/>.
    /// </summary>
    public bool StructuralComparison { get; set; }

    /// <summary>
    /// Clock used for staleness and eviction; swap for a fake in tests. Default:
    /// <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// Per-prefix defaults registered with <see cref="DejaClient.SetDefaults"/>. Every property left
/// <see langword="null"/> falls through to a shorter matching prefix, then to
/// <see cref="DejaOptions"/>. Per-query values on <see cref="QueryParameters{T}"/> beat both.
/// </summary>
public sealed class QueryDefaults
{
    /// <summary>See <see cref="DejaOptions.DefaultStaleTime"/>.</summary>
    public TimeSpan? StaleTime { get; set; }

    /// <summary>See <see cref="DejaOptions.DefaultCacheTime"/>.</summary>
    public TimeSpan? CacheTime { get; set; }

    /// <summary>See <see cref="DejaOptions.DefaultRefetchOnMount"/>.</summary>
    public RefetchOnMount? RefetchOnMount { get; set; }
}
