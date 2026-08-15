using System.Collections.Concurrent;

namespace Deja;

/// <summary>
/// Tracks a single asynchronous read and exposes its lifecycle as bindable state, notifying its
/// attached listener as it advances so the owning component can re-render. A newer
/// <see cref="Execute(QueryParameters{T})"/> supersedes (and cancels) an older in-flight one, and concurrent calls
/// sharing a <see cref="QueryParameters{T}.QueryKey"/> join the same execution instead of fetching
/// twice.
/// </summary>
/// <remarks>
/// Inherit <see cref="DejaComponentBase"/> in the owning component and the attachment is made (and
/// released) for you; see <see cref="IDejaObservable"/> for the single-owner rule.
/// </remarks>
/// <typeparam name="T">The type of the fetched data.</typeparam>
public class Query<T> : DejaObservable, IDisposable, ICacheClientConsumer, IComponentLifetimeConsumer
{
    // Join semantics for keyed queries without a cache client. With a client, in-flight
    // deduplication lives on the cache entry instead, shared across every component.
    private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlightExecutions = new(StringComparer.Ordinal);

    // Each Execute cancels and replaces this, so a newer load supersedes an older in-flight one:
    // frees the server query and prevents a slow stale response from overwriting fresh data.
    private CancellationTokenSource? _executionCts;
    private bool _disposed;

    private CacheEntry<T>? _entry;
    private IDisposable? _entrySubscription;
    private TimeSpan _staleTime;
    private Func<T, T>? _select;

    // Remembered so Refetch can re-run the last Execute; refetch copies are never stored here,
    // which is what keeps Refetch overrides one-shot.
    private QueryParameters<T>? _lastParameters;

    // The cached-path analogue of _executionCts: a completed await belonging to an older
    // generation must not run callbacks or touch shared flags.
    private int _cachedGeneration;

    /// <summary>Creates a query that resolves its cache client from the owning component (if any).</summary>
    public Query()
    {
    }

    /// <summary>Creates a query bound to <paramref name="client"/>, for use outside a component.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public Query(DejaClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Client = client;
    }

    // Set by the constructor, by DejaComponentBase when it discovers the query, or per call via
    // QueryParameters<T>.Client (which takes precedence). Null means the uncached path.
    internal DejaClient? Client { get; set; }

    DejaClient? ICacheClientConsumer.Client
    {
        get => Client;
        set => Client = value;
    }

    // Handed over by DejaComponentBase when it discovers this query. The default,
    // CancellationToken.None, is the right fallback for a query used outside a component.
    private CancellationToken ComponentToken { get; set; }

    void IComponentLifetimeConsumer.SetComponentToken(CancellationToken token) => ComponentToken = token;

    // An explicit per-call token replaces the component's rather than combining with it.
    private CancellationToken ResolveToken(QueryParameters<T> parameters)
        => parameters.CancellationToken ?? ComponentToken;

    /// <summary>True while any execution is loading.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>True when the most recent execution failed. Cleared by a successful refetch.</summary>
    public bool IsError { get; private set; }

    /// <summary>The failure message of the most recent execution, when <see cref="IsError"/> is true.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>The most recently fetched data.</summary>
    public T? Data { get; private set; }

    /// <summary>How many times the query has been executed.</summary>
    public int ReFetchCount { get; private set; }

    /// <summary>True while an execution after the first is loading. Use it for subtle refresh indicators.</summary>
    public bool IsReFetching { get; private set; }

    /// <summary>
    /// When the current <see cref="Data"/> was fetched (cached path only); <see langword="null"/>
    /// on the uncached path or before the first result.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>
    /// True while <see cref="Data"/> was served from the cache and no fresh fetch has completed
    /// during this query's subscription; false once fresh data arrives.
    /// </summary>
    public bool IsCachedData { get; private set; }

    /// <summary>
    /// True when the observed cache entry is invalidated or older than the effective stale time
    /// (cached path only; always false on the uncached path).
    /// </summary>
    public bool IsStale => _entry is { } entry && (entry.IsInvalidated || entry.IsStaleBy(_staleTime));

    /// <summary>
    /// Runs a keyed query: the shorthand for the common case, equivalent to
    /// <c>Execute(new QueryParameters&lt;T&gt; { QueryKey = key, QueryFunction = queryFunction })</c>
    /// with anything else set by <paramref name="configure"/>. <typeparamref name="T"/> comes from
    /// this query, so it is not restated at the call site:
    /// <c>_todos.Execute("todos", Api.GetTodosAsync)</c>.
    /// </summary>
    /// <param name="key">
    /// The cache key; a string converts implicitly (<c>"todos"</c> means <c>QueryKey.Of("todos")</c>).
    /// Pass <see langword="null"/> for the uncached path, where a newer call supersedes the older one.
    /// </param>
    /// <param name="queryFunction">See <see cref="QueryParameters{T}.QueryFunction"/>.</param>
    /// <param name="configure">
    /// Optional hook to set anything else — callbacks, <see cref="QueryParameters{T}.StaleTime"/>,
    /// <see cref="QueryParameters{T}.Select"/> — on the parameters before they run.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="queryFunction"/> is <see langword="null"/>.</exception>
    public Task Execute(
        QueryKey? key,
        Func<CancellationToken, Task<T>> queryFunction,
        Action<QueryParameters<T>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(queryFunction);

        var parameters = new QueryParameters<T>
        {
            QueryKey = key,
            QueryFunction = queryFunction,
        };

        configure?.Invoke(parameters);

        return Execute(parameters);
    }

    /// <summary>
    /// Runs an unkeyed query — the shorthand for a fetch that never touches the cache, where a
    /// newer call supersedes (and cancels) an older in-flight one.
    /// </summary>
    /// <param name="queryFunction">See <see cref="QueryParameters{T}.QueryFunction"/>.</param>
    /// <param name="configure">Optional hook to set callbacks and other parameters before they run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="queryFunction"/> is <see langword="null"/>.</exception>
    public Task Execute(
        Func<CancellationToken, Task<T>> queryFunction,
        Action<QueryParameters<T>>? configure = null)
        => Execute(key: null, queryFunction, configure);

    /// <summary>
    /// Runs the query described by <paramref name="parameters"/>. With a
    /// <see cref="QueryParameters{T}.QueryKey"/> and a <see cref="DejaClient"/> (from
    /// <c>AddDeja()</c> via the owning component, the constructor, or
    /// <see cref="QueryParameters{T}.Client"/>), the shared cache path runs: cached data renders
    /// instantly, staleness decides whether to refetch in the background, and concurrent same-key
    /// calls from any component join one in-flight fetch. With a key but no client, a concurrent
    /// same-key call on this instance joins the in-flight execution (the pre-cache behavior).
    /// Without a key, a newer call supersedes (and cancels) the older one.
    /// </summary>
    public Task Execute(QueryParameters<T> parameters)
    {
        if (parameters is null || _disposed) return Task.CompletedTask;

        _lastParameters = parameters;
        return ExecuteCore(parameters);
    }

    /// <summary>
    /// Re-runs the last <see cref="Execute(QueryParameters{T})"/> with a forced fresh fetch:
    /// staleness, <see cref="QueryParameters{T}.RefetchOnMount"/> and
    /// <see cref="QueryParameters{T}.Enabled"/> are bypassed — a manual refetch is an explicit
    /// request, not a mount policy. On the cached path the result updates the shared entry, so
    /// every component on the key re-renders; a concurrent same-key fetch is joined rather than
    /// duplicated. Does nothing before the first <c>Execute</c> or after disposal.
    /// </summary>
    /// <param name="parameters">
    /// Optional overrides for this call only — see <see cref="RefetchParameters{T}"/>. The key and
    /// fetch function always come from the last <c>Execute</c>; a later bare <c>Refetch()</c> is
    /// unaffected by earlier overrides.
    /// </param>
    public Task Refetch(RefetchParameters<T>? parameters = null)
    {
        if (_disposed || _lastParameters is not { } last) return Task.CompletedTask;

        var refetch = last.Clone();
        refetch.Enabled = true;
        refetch.RefetchOnMount = RefetchOnMount.Always;
        parameters?.ApplyTo(refetch);

        return ExecuteCore(refetch);
    }

    private async Task ExecuteCore(QueryParameters<T> parameters)
    {
        if (parameters.QueryKey is null)
        {
            await ExecuteInternal(parameters);
            return;
        }

        var client = parameters.Client ?? Client;
        if (client is not null)
        {
            await ExecuteCached(client, parameters.QueryKey, parameters);
            return;
        }

        var queryKey = parameters.QueryKey.Hash;

        // Caution: a same-key call made while one is in flight JOINS that execution — this call's
        // parameters are dropped and no supersede-cancel happens. Don't reuse a key across calls
        // whose parameters can differ; omit the key to supersede the in-flight load instead.
        var lazyExecution = _inFlightExecutions.GetOrAdd(
            queryKey,
            _ => new Lazy<Task>(() => ExecuteInternal(parameters), LazyThreadSafetyMode.ExecutionAndPublication));

        var execution = lazyExecution.Value;

        try
        {
            await execution;
        }
        finally
        {
            if (execution.IsCompleted)
            {
                _inFlightExecutions.TryRemove(new KeyValuePair<string, Lazy<Task>>(queryKey, lazyExecution));
            }
        }
    }

    // The shared-cache path. Unlike the uncached path, a successful fetch never writes
    // Query<T>.Data directly — it writes the entry, and this query updates because it subscribes
    // to the entry, exactly like another component's query on the same key. One code path, so two
    // components sharing a key cannot drift.
    private async Task ExecuteCached(DejaClient client, QueryKey key, QueryParameters<T> parameters)
    {
        var generation = unchecked(++_cachedGeneration);
        var entry = client.GetOrCreateEntry<T>(key);

        if (!ReferenceEquals(_entry, entry))
        {
            // Key changed (or first cached run): leave the old entry — cancelling its fetch if we
            // were the last subscriber — and observe the new one. The old entry's late result no
            // longer touches this query, so a slow stale response cannot overwrite fresh data.
            _entrySubscription?.Dispose();
            _entry = entry;
            _entrySubscription = entry.Subscribe(OnEntryChanged);
        }

        _select = parameters.Select;
        _staleTime = client.ResolveStaleTime(key, parameters.StaleTime);
        entry.SetCacheTime(client.ResolveCacheTime(key, parameters.CacheTime));
        var refetchOnMount = client.ResolveRefetchOnMount(key, parameters.RefetchOnMount);

        if (parameters.QueryFunction is not null)
        {
            // Remembered even when this Execute doesn't fetch, so a later invalidation can.
            entry.SetLastFetch(parameters.QueryFunction);
        }

        ReFetchCount++;

        var hasData = entry.TryGetData(out var cached);
        if (hasData)
        {
            // Instant render from cache; the staleness check below may still refetch.
            Data = ApplySelect(cached);
            UpdatedAt = entry.UpdatedAt;
            IsCachedData = true;
            IsError = false;
            ErrorMessage = null;
        }
        else if (parameters.PlaceholderData is not null)
        {
            // Rendered while the first fetch runs; never written to the cache.
            Data = parameters.PlaceholderData;
            IsCachedData = false;
        }

        var shouldFetch = (parameters.Enabled ?? true) &&
                          parameters.QueryFunction is not null &&
                          ShouldFetch(entry, hasData, refetchOnMount);

        if (!shouldFetch)
        {
            // Another component's fetch may still be in flight; mirror it instead of clearing.
            IsLoading = entry.IsFetching;
            IsReFetching = entry.IsFetching && hasData;
            NotifyChanged();
            return;
        }

        IsLoading = true;
        IsReFetching = hasData || ReFetchCount > 1;
        NotifyChanged();

        var fetchTask = entry.GetOrStartFetch(parameters.QueryFunction!);
        var cancelled = false;
        var callerToken = ResolveToken(parameters);

        try
        {
            // The caller's token is deliberately NOT linked into the shared fetch — one component
            // unmounting must not abort another's data. Observing the shared task through the
            // token detaches this caller without cancelling the request.
            await fetchTask.WaitAsync(callerToken);

            if (IsSupersededCached(generation, entry))
            {
                cancelled = true;
            }
            else
            {
                // Data already arrived via OnEntryChanged — the entry notifies subscribers before
                // completing the task — so only this caller's callbacks remain.
                await InvokeSuccessCallbacks(parameters);
            }
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested || fetchTask.IsCanceled)
        {
            // This caller detached, or the shared fetch was cancelled. Not an error: no callbacks.
            cancelled = true;
        }
        catch (Exception e)
        {
            if (IsSupersededCached(generation, entry))
            {
                cancelled = true;
            }
            else
            {
                var handled = await InvokeErrorCallbacks(parameters, e);
                if (!handled)
                {
                    // Same contract as the uncached path: an unobserved failure must not vanish.
                    throw new InvalidOperationException("Query failed", e);
                }
            }
        }
        finally
        {
            if (!IsSupersededCached(generation, entry))
            {
                // Mirror the entry rather than forcing false: a newer fetch may be running.
                IsLoading = entry.IsFetching;
                IsReFetching = entry.IsFetching && entry.HasData;
                NotifyChanged();
            }

            if (!cancelled)
            {
                await InvokeSettledCallbacks(parameters);
            }
        }
    }

    private bool ShouldFetch(CacheEntry<T> entry, bool hasData, RefetchOnMount refetchOnMount)
    {
        if (!hasData) return true;

        // Invalidation trumps RefetchOnMount.Never: it is an explicit request, not a mount policy.
        if (entry.IsInvalidated) return true;

        return refetchOnMount switch
        {
            RefetchOnMount.Always => true,
            RefetchOnMount.Never => false,
            _ => entry.IsStaleBy(_staleTime),
        };
    }

    // A cached await that resumes after a newer Execute started, after the key changed, or after
    // disposal must not run callbacks or touch the shared flags.
    private bool IsSupersededCached(int generation, CacheEntry<T> entry)
        => _disposed || _cachedGeneration != generation || !ReferenceEquals(_entry, entry);

    // How every cache-entry change — from this query's fetch or any other component's — reaches
    // this query's bindable state. Runs on whatever thread the change happened on; the attached
    // component marshals the render itself.
    private void OnEntryChanged(CacheEntryChangeKind kind)
    {
        if (_disposed || _entry is not { } entry) return;

        switch (kind)
        {
            case CacheEntryChangeKind.DataUpdated:
                if (entry.TryGetData(out var data))
                {
                    Data = ApplySelect(data);
                    UpdatedAt = entry.UpdatedAt;
                    IsCachedData = false;
                    IsError = false;
                    ErrorMessage = null;
                }

                break;
            case CacheEntryChangeKind.ErrorUpdated:
                // Existing data stays on screen; the component decides how to surface the error.
                IsError = true;
                ErrorMessage = entry.Error?.Message;
                break;
            case CacheEntryChangeKind.FetchingChanged:
                IsLoading = entry.IsFetching;
                IsReFetching = entry.IsFetching && entry.HasData;
                break;
            default:
                // Invalidated: IsStale is computed, so there is nothing to copy — just re-render.
                break;
        }

        NotifyChanged();
    }

    private T? ApplySelect(T? data)
    {
        if (data is null || _select is null) return data;

        return _select(data);
    }

    private async Task ExecuteInternal(QueryParameters<T> parameters)
    {
        // Supersede any previous in-flight execution: the new one gets a fresh token linked to the
        // caller's, and cancelling the previous token aborts the older request down to the server.
        var previousCts = _executionCts;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ResolveToken(parameters));
        _executionCts = cts;

        // Captured before awaiting: Dispose() may dispose cts during CancelAsync's yield, after
        // which cts.Token throws. The captured token stays valid and already cancelled, so the
        // fetch below exits through the cancellation path.
        var token = cts.Token;

        if (previousCts is not null)
        {
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        ReFetchCount++;

        IsLoading = true;

        if (ReFetchCount > 1)
        {
            IsReFetching = true;
        }

        // One notification for the whole transition, once every flag above is coherent.
        NotifyChanged();

        var cancelled = false;

        try
        {
            T data = await FetchWithTimeoutRetryAsync(parameters, token);

            if (token.IsCancellationRequested)
            {
                // A QueryFunction that ignores its token can't observe the supersede; discard its
                // stale result so it doesn't overwrite the newer load's data.
                cancelled = true;
            }
            else
            {
                // A successful refetch clears a previous failure so error UI doesn't outlive it.
                IsError = false;
                ErrorMessage = null;
                Data = data;

                // Notify before the callbacks so the listener never renders against data the query
                // has accepted but not yet published.
                NotifyChanged();

                await InvokeSuccessCallbacks(parameters);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer load, or cancelled because the component was disposed. Not an
            // error: no error state and no callbacks — the superseding load drives the next update.
            cancelled = true;
        }
        catch (Exception e)
        {
            IsError = true;
            ErrorMessage = e.Message;
            NotifyChanged();

            var handled = await InvokeErrorCallbacks(parameters, e);
            if (!handled)
            {
                // Nothing observed this failure, so propagate rather than lose it. A callback that
                // ran means the caller surfaced it, and Execute must not escape: it is typically
                // awaited from OnInitializedAsync, where a rethrow becomes an unhandled render
                // exception that crashes the component over a transient network error.
                throw new InvalidOperationException("Query failed", e);
            }
        }
        finally
        {
            // Only the current execution may touch the shared loading flags: a superseded one
            // finishing early must not clear the spinner the live load just turned on.
            var isCurrent = ReferenceEquals(_executionCts, cts);
            if (isCurrent)
            {
                _executionCts = null;
            }
            cts.Dispose();

            if (isCurrent)
            {
                IsLoading = false;
                IsReFetching = false;
                NotifyChanged();
            }

            if (!cancelled)
            {
                await InvokeSettledCallbacks(parameters);
            }
        }
    }

    private static async Task<T> FetchAsync(QueryParameters<T> parameters, CancellationToken token)
    {
        if (parameters.QueryFunction is not null)
        {
            return await parameters.QueryFunction(token);
        }

        throw new InvalidOperationException("QueryParameters requires QueryFunction.");
    }

    // The retry policy itself lives in TimeoutRetry, shared with cache-entry fetches.
    private static Task<T> FetchWithTimeoutRetryAsync(QueryParameters<T> parameters, CancellationToken token)
        => TimeoutRetry.FetchAsync(t => FetchAsync(parameters, t), token);

    private async Task InvokeSuccessCallbacks(QueryParameters<T> parameters)
    {
        if (parameters.OnSuccessAsync is not null)
        {
            await parameters.OnSuccessAsync(Data);
        }

        parameters.OnSuccess?.Invoke(Data);
    }

    // Returns true when at least one error callback observed the exception.
    private static async Task<bool> InvokeErrorCallbacks(QueryParameters<T> parameters, Exception e)
    {
        var handled = false;

        if (e is DisplayUserException displayUserException)
        {
            if (parameters.OnDisplayUserErrorAsync is not null)
            {
                await parameters.OnDisplayUserErrorAsync(displayUserException);
                handled = true;
            }

            if (parameters.OnDisplayUserError is not null)
            {
                parameters.OnDisplayUserError(displayUserException);
                handled = true;
            }
        }

        if (parameters.OnErrorAsync is not null)
        {
            await parameters.OnErrorAsync(e);
            handled = true;
        }

        if (parameters.OnError is not null)
        {
            parameters.OnError(e);
            handled = true;
        }

        return handled;
    }

    private async Task InvokeSettledCallbacks(QueryParameters<T> parameters)
    {
        if (parameters.OnSettledAsync is not null)
        {
            await parameters.OnSettledAsync(Data);
        }

        parameters.OnSettled?.Invoke(Data);
    }

    /// <summary>
    /// Resets the query state. With <paramref name="cancelCurrentRequest"/> the in-flight execution
    /// is also cancelled without disposing the query, so it can still be executed again.
    /// </summary>
    public void ClearData(bool cancelCurrentRequest)
    {
        if (cancelCurrentRequest)
        {
            _executionCts?.Cancel();

            // The cached path has no per-execution token to cancel — the fetch belongs to the
            // shared entry and may be feeding other components. Retiring the generation instead
            // leaves an in-flight cached await superseded: no callbacks, and it cannot re-publish
            // the data this reset just cleared.
            unchecked
            {
                _cachedGeneration++;
            }
        }

        ClearData();
    }

    /// <summary>
    /// Resets the query state without cancelling an in-flight execution. Resets only this query's
    /// view — a cache entry it observes is untouched, so the next keyed
    /// <see cref="Execute(QueryParameters{T})"/> serves the cached data again; use
    /// <see cref="DejaClient.Remove"/> to drop the entry itself.
    /// </summary>
    public void ClearData()
    {
        Data = default;
        IsLoading = false;
        IsError = false;
        ErrorMessage = null;
        ReFetchCount = 0;
        IsReFetching = false;
        UpdatedAt = null;
        IsCachedData = false;

        // One notification covering the whole reset, so a component binding IsError doesn't keep
        // rendering an error this call already cleared.
        NotifyChanged();
    }

    /// <summary>
    /// Cancels any in-flight execution. Call from the owning component's Dispose so navigating
    /// away aborts the request (and the server query) instead of letting it run to completion.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Standard dispose pattern; cancels and disposes the in-flight execution's token source.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (!disposing) return;

        // Callback closures must not outlive the component that created them.
        _lastParameters = null;

        var cts = _executionCts;
        _executionCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        // Leaving the entry may cancel its fetch (if this was the last subscriber) and starts its
        // eviction clock; the entry and its data survive for the next component to mount on.
        _entrySubscription?.Dispose();
        _entrySubscription = null;
        _entry = null;
    }
}

/// <summary>Describes one execution of a <see cref="Query{T}"/>: how to fetch and which callbacks to run.</summary>
/// <typeparam name="T">The type of the fetched data.</typeparam>
public class QueryParameters<T>
{
    /// <summary>
    /// Optional structured key; concurrent executions sharing a key join instead of fetching
    /// twice. String literals convert implicitly (<c>QueryKey = "todos"</c> means
    /// <c>QueryKey.Of("todos")</c>); build hierarchical keys with <see cref="Deja.QueryKey.Of"/>.
    /// </summary>
    public QueryKey? QueryKey { get; set; }

    /// <summary>
    /// The function that fetches the data. <see cref="Query{T}"/> passes a token that is cancelled
    /// when a newer load supersedes this one or when the query is disposed; honour it when the
    /// underlying call supports cancellation. A fetch that cannot observe cancellation ignores the
    /// token with a discard: <c>QueryFunction = _ => FetchAsync()</c>.
    /// </summary>
    public Func<CancellationToken, Task<T>>? QueryFunction { get; set; }

    /// <summary>
    /// Caller-owned lifetime token, linked into the per-execution token handed to
    /// <see cref="QueryFunction"/>. Leave <see langword="null"/> (the default) inside a
    /// <see cref="DejaComponentBase"/> component: the query then uses the component's own lifetime
    /// token, so the execution is cancelled on dispose with nothing to wire. Set it to scope an
    /// execution to something narrower than the component (a search box's own source, say); a
    /// value set here replaces the ambient token rather than combining with it. Outside a
    /// component, <see langword="null"/> means no caller-owned cancellation.
    /// </summary>
    public CancellationToken? CancellationToken { get; set; }

    /// <summary>Async callback invoked when the query succeeds.</summary>
    public Func<T?, Task>? OnSuccessAsync { get; set; }

    /// <summary>Callback invoked when the query succeeds.</summary>
    public Action<T?>? OnSuccess { get; set; }

    /// <summary>Async callback invoked when the query fails.</summary>
    public Func<Exception, Task>? OnErrorAsync { get; set; }

    /// <summary>Callback invoked when the query fails.</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>Async callback invoked when the query fails with a <see cref="DisplayUserException"/>.</summary>
    public Func<DisplayUserException, Task>? OnDisplayUserErrorAsync { get; set; }

    /// <summary>Callback invoked when the query fails with a <see cref="DisplayUserException"/>.</summary>
    public Action<DisplayUserException>? OnDisplayUserError { get; set; }

    /// <summary>Async callback invoked when the query settles (success or handled error, not cancellation).</summary>
    public Func<T?, Task>? OnSettledAsync { get; set; }

    /// <summary>Callback invoked when the query settles (success or handled error, not cancellation).</summary>
    public Action<T?>? OnSettled { get; set; }

    /// <summary>
    /// Cache client override for this execution. Usually left <see langword="null"/>: components
    /// inheriting <see cref="DejaComponentBase"/> get the registered client handed to their
    /// queries automatically.
    /// </summary>
    public DejaClient? Client { get; set; }

    /// <summary>Overrides <see cref="DejaOptions.DefaultStaleTime"/> for this query.</summary>
    public TimeSpan? StaleTime { get; set; }

    /// <summary>Overrides <see cref="DejaOptions.DefaultCacheTime"/> for this query's entry.</summary>
    public TimeSpan? CacheTime { get; set; }

    /// <summary>Overrides <see cref="DejaOptions.DefaultRefetchOnMount"/> for this query.</summary>
    public RefetchOnMount? RefetchOnMount { get; set; }

    /// <summary>
    /// When <see langword="false"/>, <c>Execute</c> serves cached data but never fetches — for
    /// queries whose inputs aren't ready yet. <see langword="null"/> (the default) means enabled.
    /// Cached path only.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Transforms the cached value before it is published to <see cref="Query{T}.Data"/> — per
    /// query, so two components can project one entry differently. The cache always stores the
    /// raw value. Cached path only.
    /// </summary>
    public Func<T, T>? Select { get; set; }

    /// <summary>
    /// Rendered as <see cref="Query{T}.Data"/> while the first fetch runs and the cache is empty;
    /// never written to the cache. Cached path only.
    /// </summary>
    public T? PlaceholderData { get; set; }

    // Refetch mutates a copy (forced fetch, one-shot overrides), never the caller's instance.
    internal QueryParameters<T> Clone() => new()
    {
        QueryKey = QueryKey,
        QueryFunction = QueryFunction,
        CancellationToken = CancellationToken,
        OnSuccessAsync = OnSuccessAsync,
        OnSuccess = OnSuccess,
        OnErrorAsync = OnErrorAsync,
        OnError = OnError,
        OnDisplayUserErrorAsync = OnDisplayUserErrorAsync,
        OnDisplayUserError = OnDisplayUserError,
        OnSettledAsync = OnSettledAsync,
        OnSettled = OnSettled,
        Client = Client,
        StaleTime = StaleTime,
        CacheTime = CacheTime,
        RefetchOnMount = RefetchOnMount,
        Enabled = Enabled,
        Select = Select,
        PlaceholderData = PlaceholderData,
    };
}

/// <summary>
/// The parameters a <see cref="Query{T}.Refetch"/> may override, for that call only. The identity
/// of the query — key, fetch function, client — always comes from the last <c>Execute</c>:
/// changing those is a new query, not a refetch. A property left <see langword="null"/> keeps the
/// value the last <c>Execute</c> used.
/// </summary>
/// <typeparam name="T">The type of the fetched data.</typeparam>
public class RefetchParameters<T>
{
    /// <summary>Replaces <see cref="QueryParameters{T}.CancellationToken"/> for this refetch.</summary>
    public CancellationToken? CancellationToken { get; set; }

    /// <summary>Replaces <see cref="QueryParameters{T}.OnSuccessAsync"/> for this refetch.</summary>
    public Func<T?, Task>? OnSuccessAsync { get; set; }

    /// <summary>Replaces <see cref="QueryParameters{T}.OnSuccess"/> for this refetch.</summary>
    public Action<T?>? OnSuccess { get; set; }

    /// <summary>Replaces <see cref="QueryParameters{T}.OnErrorAsync"/> for this refetch.</summary>
    public Func<Exception, Task>? OnErrorAsync { get; set; }

    /// <summary>Replaces <see cref="QueryParameters{T}.OnError"/> for this refetch.</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>Replaces <see cref="QueryParameters{T}.OnDisplayUserErrorAsync"/> for this refetch.</summary>
    public Func<DisplayUserException, Task>? OnDisplayUserErrorAsync { get; set; }

    /// <summary>Replaces <see cref="QueryParameters{T}.OnDisplayUserError"/> for this refetch.</summary>
    public Action<DisplayUserException>? OnDisplayUserError { get; set; }

    /// <summary>Replaces <see cref="QueryParameters{T}.OnSettledAsync"/> for this refetch.</summary>
    public Func<T?, Task>? OnSettledAsync { get; set; }

    /// <summary>Replaces <see cref="QueryParameters{T}.OnSettled"/> for this refetch.</summary>
    public Action<T?>? OnSettled { get; set; }

    /// <summary>
    /// Replaces <see cref="QueryParameters{T}.StaleTime"/> for this refetch. The refetch itself
    /// always fetches; this only changes how soon the fresh result counts as stale again.
    /// </summary>
    public TimeSpan? StaleTime { get; set; }

    /// <summary>Replaces <see cref="QueryParameters{T}.CacheTime"/> for this refetch.</summary>
    public TimeSpan? CacheTime { get; set; }

    internal void ApplyTo(QueryParameters<T> target)
    {
        if (CancellationToken is not null) target.CancellationToken = CancellationToken;
        if (OnSuccessAsync is not null) target.OnSuccessAsync = OnSuccessAsync;
        if (OnSuccess is not null) target.OnSuccess = OnSuccess;
        if (OnErrorAsync is not null) target.OnErrorAsync = OnErrorAsync;
        if (OnError is not null) target.OnError = OnError;
        if (OnDisplayUserErrorAsync is not null) target.OnDisplayUserErrorAsync = OnDisplayUserErrorAsync;
        if (OnDisplayUserError is not null) target.OnDisplayUserError = OnDisplayUserError;
        if (OnSettledAsync is not null) target.OnSettledAsync = OnSettledAsync;
        if (OnSettled is not null) target.OnSettled = OnSettled;
        if (StaleTime is not null) target.StaleTime = StaleTime;
        if (CacheTime is not null) target.CacheTime = CacheTime;
    }
}

/// <summary>
/// Controls whether a keyed <c>Execute</c> that finds cached data also refetches in the
/// background. A key with no cached data always fetches, regardless of this setting.
/// </summary>
public enum RefetchOnMount
{
    /// <summary>Serve the cache and never refetch on mount. Invalidation still refetches.</summary>
    Never,

    /// <summary>Refetch in the background only when the data is older than the stale time.</summary>
    IfStale,

    /// <summary>Always refetch in the background, even when the data is still fresh.</summary>
    Always,
}
