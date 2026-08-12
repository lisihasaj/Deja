namespace Deja;

/// <summary>
/// One cache entry per key: the data, the shared in-flight fetch every same-key query joins, and
/// the live subscriber list. The entry — not the query — is the multicast point: each subscribed
/// <see cref="Query{T}"/> observes it independently and still notifies only its own component, so
/// components sharing a key are updated by the entry, never by one another.
/// </summary>
/// <remarks>
/// <b>Thread safety.</b> State transitions run under <c>_lock</c>, kept strictly short and never
/// held across an <c>await</c>. Subscriber notification happens outside the lock, over a snapshot
/// of the subscriber list, so a subscriber disposing mid-notify cannot deadlock —
/// <see cref="DejaComponentBase"/> already tolerates a notification arriving during disposal.
/// </remarks>
internal sealed class CacheEntry<T> : ICacheEntry
{
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly bool _structuralComparison;
    private readonly List<Subscription> _subscribers = [];

    private Func<CancellationToken, Task<T>>? _lastFetch;
    private TaskCompletionSource<T>? _inFlight;
    private CancellationTokenSource? _inFlightCts;
    private TimeSpan _cacheTime;

    internal CacheEntry(QueryKey key, TimeProvider time, bool structuralComparison, TimeSpan cacheTime)
    {
        Key = key;
        _time = time;
        _structuralComparison = structuralComparison;
        _cacheTime = cacheTime;
        LastAccessedAt = time.GetUtcNow();

        // Born with zero subscribers (e.g. created by SetData), so eviction-eligible until the
        // first one arrives.
        EvictAt = LastAccessedAt + cacheTime;
    }

    public QueryKey Key { get; }

    public Type DataType => typeof(T);

    /// <summary>The last successful result; only meaningful while <see cref="HasData"/> is true.</summary>
    internal T? Data { get; private set; }

    /// <summary>True once a successful result has been stored.</summary>
    internal bool HasData { get; private set; }

    /// <summary>When the last successful result was stored; drives staleness.</summary>
    internal DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>The last failure, so a remounting query can render the error.</summary>
    internal Exception? Error { get; private set; }

    /// <summary>When the last failure was recorded.</summary>
    internal DateTimeOffset? ErrorUpdatedAt { get; private set; }

    public bool IsInvalidated { get; private set; }

    // Reference read; a benign race with the fetch finishing is acceptable everywhere this is used.
    public bool IsFetching => _inFlight is not null;

    public bool CanRefetch => _lastFetch is not null;

    public DateTimeOffset? EvictAt { get; private set; }

    public DateTimeOffset LastAccessedAt { get; private set; }

    public int SubscriberCount
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>
    /// Registers a query as an observer, clearing any pending eviction. Disposing the handle
    /// unsubscribes; when the last subscriber leaves, the in-flight fetch is cancelled and the
    /// eviction clock starts.
    /// </summary>
    internal IDisposable Subscribe(Action<CacheEntryChangeKind> onChange)
    {
        var subscription = new Subscription(this, onChange);

        lock (_lock)
        {
            _subscribers.Add(subscription);
            EvictAt = null;
        }

        return subscription;
    }

    /// <summary>Reads the stored data coherently with <see cref="HasData"/>.</summary>
    internal bool TryGetData(out T? data)
    {
        lock (_lock)
        {
            data = Data;
            return HasData;
        }
    }

    /// <summary>
    /// Adopts the effective cache time resolved by the most recent keyed <c>Execute</c>; used when
    /// the subscriber count next drops to zero.
    /// </summary>
    internal void SetCacheTime(TimeSpan cacheTime)
    {
        lock (_lock)
        {
            _cacheTime = cacheTime;
        }
    }

    /// <summary>
    /// Remembers how to fetch this key so invalidation can refetch it later, even when the
    /// current <c>Execute</c> serves fresh cache without fetching.
    /// </summary>
    internal void SetLastFetch(Func<CancellationToken, Task<T>> fetch)
    {
        lock (_lock)
        {
            _lastFetch = fetch;
        }
    }

    /// <summary>
    /// Stores a successful result, clears error and invalidation state, and notifies every
    /// subscriber. With structural comparison on, an equal value keeps the old reference and
    /// skips the notification (freshness is still recorded).
    /// </summary>
    internal void SetData(T data)
    {
        Action<CacheEntryChangeKind>[] listeners;

        lock (_lock)
        {
            var now = _time.GetUtcNow();

            if (_structuralComparison && HasData && EqualityComparer<T>.Default.Equals(Data, data))
            {
                UpdatedAt = now;
                IsInvalidated = false;
                Error = null;
                ErrorUpdatedAt = null;
                return;
            }

            Data = data;
            HasData = true;
            UpdatedAt = now;
            IsInvalidated = false;
            Error = null;
            ErrorUpdatedAt = null;
            listeners = SnapshotListeners();
        }

        Notify(listeners, CacheEntryChangeKind.DataUpdated);
    }

    /// <summary>Records a failure (keeping any previous data) and notifies every subscriber.</summary>
    internal void SetError(Exception error)
    {
        Action<CacheEntryChangeKind>[] listeners;

        lock (_lock)
        {
            Error = error;
            ErrorUpdatedAt = _time.GetUtcNow();
            listeners = SnapshotListeners();
        }

        Notify(listeners, CacheEntryChangeKind.ErrorUpdated);
    }

    public void Invalidate()
    {
        Action<CacheEntryChangeKind>[] listeners;

        lock (_lock)
        {
            IsInvalidated = true;
            listeners = SnapshotListeners();
        }

        Notify(listeners, CacheEntryChangeKind.Invalidated);
    }

    /// <summary>True when the stored data is at least <paramref name="staleTime"/> old (or absent).
    /// A zero stale time therefore means "always stale" — the reference model's default.</summary>
    internal bool IsStaleBy(TimeSpan staleTime)
    {
        lock (_lock)
        {
            return !HasData || UpdatedAt is not { } updatedAt || _time.GetUtcNow() - updatedAt >= staleTime;
        }
    }

    /// <summary>
    /// Joins the in-flight fetch when one is running; otherwise starts one with its own
    /// cancellation source — deliberately not linked to any caller's lifetime token, so one
    /// component unmounting cannot abort another component's data. Exactly one fetch runs under
    /// concurrent access. The returned task completes after the result (or failure) has been
    /// stored on the entry and subscribers notified.
    /// </summary>
    internal Task<T> GetOrStartFetch(Func<CancellationToken, Task<T>> fetch)
    {
        TaskCompletionSource<T> execution;
        CancellationToken token;
        Action<CacheEntryChangeKind>[] listeners;

        lock (_lock)
        {
            _lastFetch = fetch;

            if (_inFlight is not null)
            {
                return _inFlight.Task;
            }

            execution = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = execution;
            _inFlightCts = new CancellationTokenSource();
            token = _inFlightCts.Token;
            listeners = SnapshotListeners();
        }

        Notify(listeners, CacheEntryChangeKind.FetchingChanged);

        // Fire-and-forget is safe: DriveFetchAsync catches everything and routes it into the
        // TaskCompletionSource. The synchronous head runs here, so the fetch starts immediately.
        _ = DriveFetchAsync(fetch, execution, token);

        return execution.Task;
    }

    public async Task RefetchAsync()
    {
        Func<CancellationToken, Task<T>>? fetch;

        lock (_lock)
        {
            fetch = _lastFetch;
        }

        if (fetch is null)
        {
            return;
        }

        try
        {
            await GetOrStartFetch(fetch);
        }
        catch (Exception)
        {
            // Already recorded by SetError and surfaced to every subscriber; a client-triggered
            // refetch has no caller of its own to rethrow to.
        }
    }

    /// <summary>Cancels the in-flight fetch, if any. Entry data survives.</summary>
    internal void CancelInFlight()
    {
        CancellationTokenSource? cts;

        lock (_lock)
        {
            cts = _inFlightCts;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The fetch finished and disposed its source between the snapshot and the cancel.
        }
    }

    public void Touch() => LastAccessedAt = _time.GetUtcNow();

    public CacheEntryState GetState()
    {
        lock (_lock)
        {
            return new CacheEntryState
            {
                HasData = HasData,
                UpdatedAt = UpdatedAt,
                ErrorMessage = Error?.Message,
                ErrorUpdatedAt = ErrorUpdatedAt,
                IsInvalidated = IsInvalidated,
                IsFetching = _inFlight is not null,
                SubscriberCount = _subscribers.Count,
            };
        }
    }

    public void Dispose() => CancelInFlight();

    private async Task DriveFetchAsync(
        Func<CancellationToken, Task<T>> fetch,
        TaskCompletionSource<T> execution,
        CancellationToken token)
    {
        try
        {
            var data = await TimeoutRetry.FetchAsync(fetch, token);

            if (token.IsCancellationRequested)
            {
                // A fetch that ignores its token can't observe the cancellation; discard its
                // result so a cancelled entry doesn't resurrect with stale data.
                FinishFetch(execution);
                execution.TrySetCanceled(token);
            }
            else
            {
                SetData(data);
                FinishFetch(execution);
                execution.TrySetResult(data);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            FinishFetch(execution);
            execution.TrySetCanceled(token);
        }
        catch (Exception e)
        {
            SetError(e);
            FinishFetch(execution);
            execution.TrySetException(e);
        }
    }

    // Clears the in-flight slot (only if this execution still owns it) and tells subscribers the
    // fetch stopped. Runs before the TaskCompletionSource completes so joined Executes resume
    // against settled entry state.
    private void FinishFetch(TaskCompletionSource<T> execution)
    {
        Action<CacheEntryChangeKind>[]? listeners = null;

        lock (_lock)
        {
            if (ReferenceEquals(_inFlight, execution))
            {
                _inFlight = null;
                _inFlightCts?.Dispose();
                _inFlightCts = null;
                listeners = SnapshotListeners();
            }
        }

        if (listeners is not null)
        {
            Notify(listeners, CacheEntryChangeKind.FetchingChanged);
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        var wasLast = false;

        lock (_lock)
        {
            if (_subscribers.Remove(subscription) && _subscribers.Count == 0)
            {
                EvictAt = _time.GetUtcNow() + _cacheTime;
                wasLast = true;
            }
        }

        if (wasLast)
        {
            // Nothing is waiting on the result; free the connection. The next subscriber starts a
            // fresh fetch.
            CancelInFlight();
        }
    }

    private Action<CacheEntryChangeKind>[] SnapshotListeners()
    {
        var listeners = new Action<CacheEntryChangeKind>[_subscribers.Count];

        for (var i = 0; i < _subscribers.Count; i++)
        {
            listeners[i] = _subscribers[i].OnChange;
        }

        return listeners;
    }

    // Listeners are Query<T>.OnEntryChanged, which never throws (it updates properties and
    // invokes the component's fire-and-forget render callback).
    private static void Notify(Action<CacheEntryChangeKind>[] listeners, CacheEntryChangeKind kind)
    {
        foreach (var listener in listeners)
        {
            listener(kind);
        }
    }

    private sealed class Subscription(CacheEntry<T> owner, Action<CacheEntryChangeKind> onChange) : IDisposable
    {
        private CacheEntry<T>? _owner = owner;

        internal Action<CacheEntryChangeKind> OnChange { get; } = onChange;

        public void Dispose()
        {
            var owner = _owner;
            _owner = null;
            owner?.Unsubscribe(this);
        }
    }
}

/// <summary>
/// What changed on a cache entry, delivered to each subscribed <see cref="Query{T}"/> so it can
/// update its own bindable state and notify its component.
/// </summary>
internal enum CacheEntryChangeKind
{
    /// <summary>A successful result was stored (fetch success, <c>SetData</c>).</summary>
    DataUpdated,

    /// <summary>A failure was recorded.</summary>
    ErrorUpdated,

    /// <summary>The shared fetch started or finished.</summary>
    FetchingChanged,

    /// <summary>The entry was marked stale by invalidation.</summary>
    Invalidated,
}

/// <summary>
/// The non-generic surface of <see cref="CacheEntry{T}"/> that <see cref="DejaClient"/>'s registry
/// stores and operates on: matching, invalidation, refetch, eviction bookkeeping. Data access
/// stays on the generic entry. <c>Dispose</c> cancels any in-flight fetch; entry state is
/// deliberately kept afterwards so a still-subscribed query keeps rendering its last data.
/// </summary>
internal interface ICacheEntry : IDisposable
{
    /// <summary>The structured key this entry is stored under.</summary>
    QueryKey Key { get; }

    /// <summary>The <c>T</c> of the stored data, for mismatch diagnostics.</summary>
    Type DataType { get; }

    /// <summary>How many live queries observe this entry. Never evicted while positive.</summary>
    int SubscriberCount { get; }

    /// <summary>True when marked stale by invalidation and not successfully refetched since.</summary>
    bool IsInvalidated { get; }

    /// <summary>True while the shared fetch is in flight.</summary>
    bool IsFetching { get; }

    /// <summary>True when the entry has a fetch function to refetch with.</summary>
    bool CanRefetch { get; }

    /// <summary>When to evict; set when the subscriber count drops to zero, cleared on subscribe.</summary>
    DateTimeOffset? EvictAt { get; }

    /// <summary>Last read access, for least-recently-used eviction under <see cref="DejaOptions.MaxEntries"/>.</summary>
    DateTimeOffset LastAccessedAt { get; }

    /// <summary>Marks the entry stale and notifies subscribers; the next mount (or an immediate
    /// refetch, per <see cref="RefetchType"/>) fetches regardless of age.</summary>
    void Invalidate();

    /// <summary>
    /// Runs the stored fetch function (joining the in-flight fetch if one is running) and stores
    /// the result. Failures are recorded on the entry and surfaced to subscribers, not thrown.
    /// Completes without fetching when no fetch function is stored.
    /// </summary>
    Task RefetchAsync();

    /// <summary>Records a read access for least-recently-used ordering.</summary>
    void Touch();

    /// <summary>A coherent read-only snapshot for <see cref="DejaClient.GetState"/>.</summary>
    CacheEntryState GetState();
}

/// <summary>
/// A read-only snapshot of one cache entry, returned by <see cref="DejaClient.GetState"/>.
/// Values are coherent with each other at the moment of the call and do not update afterwards.
/// </summary>
public sealed record CacheEntryState
{
    /// <summary>True when the entry holds a successful result.</summary>
    public required bool HasData { get; init; }

    /// <summary>When the last successful result was stored; drives staleness.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The message of the last failure, when one is recorded.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>When the last failure was recorded.</summary>
    public DateTimeOffset? ErrorUpdatedAt { get; init; }

    /// <summary>True when the entry was invalidated and has not successfully refetched since.</summary>
    public bool IsInvalidated { get; init; }

    /// <summary>True while a shared fetch for this entry is in flight.</summary>
    public bool IsFetching { get; init; }

    /// <summary>How many live queries currently observe this entry.</summary>
    public int SubscriberCount { get; init; }
}
