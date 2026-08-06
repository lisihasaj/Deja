using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Deja;

/// <summary>
/// Tracks a single asynchronous read and exposes its lifecycle as bindable state, raising
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> as it advances so components can
/// re-render. A newer <see cref="Execute"/> supersedes (and cancels) an older in-flight one,
/// and concurrent calls sharing a <see cref="QueryParameters{T}.QueryKey"/> join the same
/// execution instead of fetching twice.
/// </summary>
/// <typeparam name="T">The type of the fetched data.</typeparam>
public class Query<T> : INotifyPropertyChanged, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlightExecutions = new(StringComparer.Ordinal);

    // Owns the cancellation for the most recent execution. Each Execute cancels and replaces it,
    // so a newer load supersedes (and aborts) an older in-flight one — this both frees the server
    // query and prevents a slow stale response from overwriting fresh data. Disposing the query
    // (e.g. when the owning component is disposed on navigation) cancels the live load.
    private CancellationTokenSource? _executionCts;
    private bool _disposed;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while the very first execution is loading. Use it for initial skeletons.</summary>
    public bool InitialLoading { get; private set; }

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
    /// Runs the query described by <paramref name="parameters"/>. When
    /// <see cref="QueryParameters{T}.QueryKey"/> is set, a concurrent same-key call joins the
    /// in-flight execution; otherwise a newer call supersedes (and cancels) the older one.
    /// </summary>
    public async Task Execute(QueryParameters<T> parameters)
    {
        if (parameters is null || _disposed) return;

        if (string.IsNullOrWhiteSpace(parameters.QueryKey))
        {
            await ExecuteInternal(parameters);
            return;
        }

        var queryKey = parameters.QueryKey!;

        // Caution: a same-key call made while one is in flight JOINS that execution — this call's
        // parameters (captured in its fetch closure) are dropped and no supersede-cancel happens.
        // Don't reuse a key across calls whose parameters can differ; omit the key to always
        // supersede the in-flight load instead.
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

    private async Task ExecuteInternal(QueryParameters<T> parameters)
    {
        // Supersede any previous in-flight execution. The new execution gets a fresh token linked
        // to the caller's lifetime token (cancelled when the component is disposed); cancelling the
        // previous token aborts the older request all the way down to the server.
        var previousCts = _executionCts;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parameters.CancellationToken);
        _executionCts = cts;

        // Capture the token before awaiting: Dispose() may dispose cts during CancelAsync's yield,
        // and cts.Token would then throw ObjectDisposedException (the captured token stays valid
        // and is already cancelled, so the fetch below exits through the cancellation path).
        var token = cts.Token;

        if (previousCts is not null)
        {
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        ReFetchCount++;

        if (ReFetchCount == 1)
        {
            InitialLoading = true;
            OnPropertyChanged(nameof(InitialLoading));
        }

        IsLoading = true;
        OnPropertyChanged(nameof(IsLoading));

        if (ReFetchCount > 1)
        {
            IsReFetching = true;
            OnPropertyChanged(nameof(IsReFetching));
        }

        var cancelled = false;

        try
        {
            T data = await FetchWithTimeoutRetryAsync(parameters, token);

            if (token.IsCancellationRequested)
            {
                // A token-less QueryFunction can't observe the supersede; discard its stale result
                // so it doesn't overwrite the newer load's data.
                cancelled = true;
            }
            else
            {
                // A successful refetch clears a previous failure so error UI doesn't outlive the recovery.
                if (IsError)
                {
                    IsError = false;
                    OnPropertyChanged(nameof(IsError));

                    ErrorMessage = null;
                    OnPropertyChanged(nameof(ErrorMessage));
                }

                Data = data;
                await InvokeSuccessCallbacks(parameters);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer load or cancelled because the component was disposed.
            // A cancelled query is not an error: don't surface it, don't run success/error/settled
            // callbacks (the superseding load — if any — drives the next UI update).
            cancelled = true;
        }
        catch (Exception e)
        {
            IsError = true;
            OnPropertyChanged(nameof(IsError));

            ErrorMessage = e.Message;
            OnPropertyChanged(nameof(ErrorMessage));

            var handled = await InvokeErrorCallbacks(parameters, e);
            if (!handled)
            {
                // No error callback observed this failure — propagate so it isn't silently lost.
                // When a callback ran, the failure is considered handled (surfaced to the user by
                // the caller) and must not escape: Execute is typically awaited from lifecycle
                // methods like OnInitializedAsync, where a rethrow becomes an unhandled render
                // exception that crashes the component over a transient network error.
                throw new InvalidOperationException("Query failed", e);
            }
        }
        finally
        {
            // Only the current (non-superseded) execution may touch the shared loading flags:
            // a superseded execution finishing early must not clear the spinner the live load
            // just turned on, and the live load must clear InitialLoading even when a superseded
            // sibling bumped ReFetchCount past 1 in the meantime.
            var isCurrent = ReferenceEquals(_executionCts, cts);
            if (isCurrent)
            {
                _executionCts = null;
            }
            cts.Dispose();

            if (isCurrent)
            {
                if (InitialLoading)
                {
                    InitialLoading = false;
                    OnPropertyChanged(nameof(InitialLoading));
                }

                IsLoading = false;
                OnPropertyChanged(nameof(IsLoading));

                if (IsReFetching)
                {
                    IsReFetching = false;
                    OnPropertyChanged(nameof(IsReFetching));
                }
            }

            if (!cancelled)
            {
                await InvokeSettledCallbacks(parameters);
            }
        }
    }

    private static async Task<T> FetchAsync(QueryParameters<T> parameters, CancellationToken token)
    {
        if (parameters.QueryFunctionWithToken is not null)
        {
            return await parameters.QueryFunctionWithToken(token);
        }

        if (parameters.QueryFunction is not null)
        {
            return await parameters.QueryFunction();
        }

        throw new InvalidOperationException(
            "QueryParameters requires QueryFunction or QueryFunctionWithToken.");
    }

    private static async Task<T> FetchWithTimeoutRetryAsync(QueryParameters<T> parameters, CancellationToken token)
    {
        try
        {
            return await FetchAsync(parameters, token);
        }
        catch (TaskCanceledException tce) when (tce.InnerException is TimeoutException && !token.IsCancellationRequested)
        {
            // HttpClient.Timeout expiry is the only TaskCanceledException that carries an inner
            // TimeoutException. HttpClient.CreateTimeoutException builds — on WASM too — exactly:
            //   TaskCanceledException("net_http_request_timedout, N")
            //    -> TimeoutException("OperationCanceled")
            //        -> TaskCanceledException/OperationCanceledException (the low-level cancel)
            // Typical cause: the browser froze an inactive tab mid-request and the timeout budget
            // elapsed before the tab resumed. Queries are idempotent reads, so retry once; on
            // resume the retry completes in milliseconds. A second timeout falls through to the
            // normal error path. Any other cancellation (including caller-owned tokens Query
            // doesn't know about) has no inner TimeoutException and must not trigger a retry.
            return await FetchAsync(parameters, token);
        }
    }

    private async Task InvokeSuccessCallbacks(QueryParameters<T> parameters)
    {
        if (parameters.OnSuccessAsync is not null)
        {
            await parameters.OnSuccessAsync(Data);
        }

        parameters.OnSuccess?.Invoke(Data);
        OnPropertyChanged(nameof(Data));
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
        }

        ClearData();
    }

    /// <summary>Resets the query state without cancelling an in-flight execution.</summary>
    public void ClearData()
    {
        Data = default;
        IsLoading = false;
        IsError = false;
        ErrorMessage = null;
        ReFetchCount = 0;
        InitialLoading = false;
        IsReFetching = false;
        OnPropertyChanged(nameof(Data));
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

        var cts = _executionCts;
        _executionCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>Describes one execution of a <see cref="Query{T}"/>: how to fetch and which callbacks to run.</summary>
/// <typeparam name="T">The type of the fetched data.</typeparam>
public class QueryParameters<T>
{
    /// <summary>Optional key; concurrent executions sharing a key join instead of fetching twice.</summary>
    public string? QueryKey { get; set; }

    /// <summary>The function that fetches the data.</summary>
    public Func<Task<T>>? QueryFunction { get; set; }

    /// <summary>
    /// Token-aware fetch function. Prefer this over <see cref="QueryFunction"/> when the underlying
    /// call supports cancellation — <see cref="Query{T}"/> passes a token that is cancelled when a
    /// newer load supersedes this one or when the query is disposed. Takes precedence over
    /// <see cref="QueryFunction"/> when both are set.
    /// </summary>
    public Func<CancellationToken, Task<T>>? QueryFunctionWithToken { get; set; }

    /// <summary>
    /// Caller-owned lifetime token (e.g. a component-scoped <see cref="CancellationTokenSource"/>
    /// cancelled in Dispose). Linked into the per-execution token handed to
    /// <see cref="QueryFunctionWithToken"/>.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }

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
}
