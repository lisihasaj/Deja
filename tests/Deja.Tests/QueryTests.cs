namespace Deja.Tests;

public class QueryTests
{
    [Fact]
    public async Task Execute_SetsData_AndTogglesLoadingFlags()
    {
        using var query = new Query<int>();
        var tcs = new TaskCompletionSource<int>();

        var execution = query.Execute(new QueryParameters<int> { QueryFunction = () => tcs.Task });

        Assert.True(query.InitialLoading);
        Assert.True(query.IsLoading);
        Assert.False(query.IsReFetching);

        tcs.SetResult(42);
        await execution;

        Assert.Equal(42, query.Data);
        Assert.False(query.InitialLoading);
        Assert.False(query.IsLoading);
        Assert.False(query.IsError);
        Assert.Equal(1, query.ReFetchCount);
    }

    [Fact]
    public async Task Execute_SecondRun_ReportsReFetching_NotInitialLoading()
    {
        using var query = new Query<int>();
        await query.Execute(new QueryParameters<int> { QueryFunction = () => Task.FromResult(1) });

        var tcs = new TaskCompletionSource<int>();
        var execution = query.Execute(new QueryParameters<int> { QueryFunction = () => tcs.Task });

        Assert.False(query.InitialLoading);
        Assert.True(query.IsReFetching);

        tcs.SetResult(2);
        await execution;

        Assert.Equal(2, query.Data);
        Assert.False(query.IsReFetching);
        Assert.Equal(2, query.ReFetchCount);
    }

    [Fact]
    public async Task Execute_WithoutErrorCallback_WrapsAndThrows()
    {
        using var query = new Query<int>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            query.Execute(new QueryParameters<int>
            {
                QueryFunction = () => Task.FromException<int>(new InvalidDataException("boom")),
            }));

        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.True(query.IsError);
        Assert.Equal("boom", query.ErrorMessage);
    }

    [Fact]
    public async Task Execute_WithErrorCallback_DoesNotThrow_AndReportsError()
    {
        using var query = new Query<int>();
        Exception? observed = null;

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = () => Task.FromException<int>(new InvalidDataException("boom")),
            OnError = e => observed = e,
        });

        Assert.IsType<InvalidDataException>(observed);
        Assert.True(query.IsError);
    }

    [Fact]
    public async Task Execute_DisplayUserException_RoutesToDisplayCallback()
    {
        using var query = new Query<int>();
        string? displayMessage = null;

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = () => Task.FromException<int>(new DisplayUserException("user-facing", "internal")),
            OnDisplayUserError = e => displayMessage = e.DisplayMessage,
        });

        Assert.Equal("user-facing", displayMessage);
        Assert.Equal("internal", query.ErrorMessage);
    }

    [Fact]
    public async Task Execute_SuccessfulRefetch_ClearsPreviousError()
    {
        using var query = new Query<int>();

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = () => Task.FromException<int>(new InvalidDataException("boom")),
            OnError = _ => { },
        });
        Assert.True(query.IsError);

        await query.Execute(new QueryParameters<int> { QueryFunction = () => Task.FromResult(7) });

        Assert.False(query.IsError);
        Assert.Null(query.ErrorMessage);
        Assert.Equal(7, query.Data);
    }

    [Fact]
    public async Task Execute_SameQueryKey_JoinsInFlightExecution()
    {
        using var query = new Query<int>();
        var calls = 0;
        var tcs = new TaskCompletionSource<int>();

        Func<Task<int>> fetch = () =>
        {
            calls++;
            return tcs.Task;
        };

        var first = query.Execute(new QueryParameters<int> { QueryKey = "k", QueryFunction = fetch });
        var second = query.Execute(new QueryParameters<int> { QueryKey = "k", QueryFunction = fetch });

        tcs.SetResult(42);
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(42, query.Data);
        Assert.Equal(1, query.ReFetchCount);
    }

    [Fact]
    public async Task Execute_WithoutKey_NewerLoadSupersedes_StaleResultDiscarded()
    {
        using var query = new Query<string>();
        var slow = new TaskCompletionSource<string>();

        var first = query.Execute(new QueryParameters<string> { QueryFunction = () => slow.Task });
        var second = query.Execute(new QueryParameters<string> { QueryFunction = () => Task.FromResult("fresh") });

        await second;
        slow.SetResult("stale");
        await first;

        Assert.Equal("fresh", query.Data);
        Assert.False(query.IsLoading);
        Assert.False(query.IsError);
    }

    [Fact]
    public async Task Execute_TokenAwareFetch_IsCancelledOnSupersede()
    {
        using var query = new Query<string>();
        var started = new TaskCompletionSource();
        var observedCancellation = new TaskCompletionSource();

        var first = query.Execute(new QueryParameters<string>
        {
            QueryFunctionWithToken = async token =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    observedCancellation.SetResult();
                    throw;
                }
                return "never";
            },
        });

        await started.Task;
        var second = query.Execute(new QueryParameters<string> { QueryFunction = () => Task.FromResult("fresh") });

        await Task.WhenAll(first, second);
        await observedCancellation.Task;

        Assert.Equal("fresh", query.Data);
    }

    [Fact]
    public async Task Execute_HttpTimeoutShape_IsRetriedOnce()
    {
        using var query = new Query<int>();
        var calls = 0;

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = () =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<int>(new TaskCanceledException("timed out", new TimeoutException()))
                    : Task.FromResult(5);
            },
        });

        Assert.Equal(2, calls);
        Assert.Equal(5, query.Data);
        Assert.False(query.IsError);
    }

    [Fact]
    public async Task Execute_SettledCallback_RunsOnSuccessAndError_ButNotOnCancellation()
    {
        using var query = new Query<int>();
        var settled = 0;

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = () => Task.FromResult(1),
            OnSettled = _ => settled++,
        });
        Assert.Equal(1, settled);

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = () => Task.FromException<int>(new InvalidDataException("boom")),
            OnError = _ => { },
            OnSettled = _ => settled++,
        });
        Assert.Equal(2, settled);

        var slow = new TaskCompletionSource<int>();
        var superseded = query.Execute(new QueryParameters<int>
        {
            QueryFunction = () => slow.Task,
            OnSettled = _ => settled++,
        });
        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = () => Task.FromResult(2),
            OnSettled = _ => settled++,
        });
        slow.SetResult(99);
        await superseded;

        // Success of the superseding load counts; the superseded one must not settle.
        Assert.Equal(3, settled);
    }

    [Fact]
    public async Task ClearData_ResetsState()
    {
        using var query = new Query<int>();
        await query.Execute(new QueryParameters<int> { QueryFunction = () => Task.FromResult(3) });

        query.ClearData();

        Assert.Equal(0, query.Data);
        Assert.Equal(0, query.ReFetchCount);
        Assert.False(query.IsLoading);
        Assert.False(query.IsError);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightExecution_AndBlocksFurtherExecutes()
    {
        var query = new Query<string>();
        var started = new TaskCompletionSource();

        var execution = query.Execute(new QueryParameters<string>
        {
            QueryFunctionWithToken = async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return "never";
            },
        });

        await started.Task;
        query.Dispose();
        await execution;

        Assert.Null(query.Data);

        // Executing a disposed query is a no-op.
        await query.Execute(new QueryParameters<string> { QueryFunction = () => Task.FromResult("late") });
        Assert.Null(query.Data);
    }

    [Fact]
    public async Task PropertyChanged_IsRaisedForData()
    {
        using var query = new Query<int>();
        var changed = new List<string?>();
        query.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await query.Execute(new QueryParameters<int> { QueryFunction = () => Task.FromResult(1) });

        Assert.Contains(nameof(Query<int>.Data), changed);
        Assert.Contains(nameof(Query<int>.IsLoading), changed);
        Assert.Contains(nameof(Query<int>.InitialLoading), changed);
    }
}
