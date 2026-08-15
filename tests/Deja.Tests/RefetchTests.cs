namespace Deja.Tests;

public class RefetchTests
{
    // Long stale time everywhere: nothing refetches on its own, so every fetch observed in these
    // tests was caused by the Refetch under test.
    private static DejaClient CreateClient()
        => new(new DejaOptions
        {
            DefaultStaleTime = TimeSpan.FromMinutes(30),
            DefaultRefetchOnMount = RefetchOnMount.IfStale,
        });

    [Fact]
    public async Task Refetch_BeforeAnyExecute_IsANoOp()
    {
        using var query = new Query<int>();

        await query.Refetch();

        Assert.False(query.IsLoading);
        Assert.Equal(0, query.ReFetchCount);
        Assert.Equal(0, query.Data);
    }

    [Fact]
    public async Task Refetch_AfterDispose_IsANoOp()
    {
        var query = new Query<int>();
        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(1) });
        query.Dispose();

        await query.Refetch();

        Assert.Equal(1, query.Data);
    }

    [Fact]
    public async Task Refetch_Uncached_ReusesLastParameters()
    {
        using var query = new Query<int>();
        var calls = 0;

        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(++calls) });
        await query.Refetch();

        Assert.Equal(2, calls);
        Assert.Equal(2, query.Data);
        Assert.Equal(2, query.ReFetchCount);
    }

    [Fact]
    public async Task Refetch_Cached_FetchesEvenWhenDataIsFresh()
    {
        using var client = CreateClient();
        using var query = new Query<int>(client);
        var calls = 0;

        var parameters = new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => Task.FromResult(++calls),
        };

        await query.Execute(parameters);

        // Fresh data + IfStale: a plain re-Execute serves the cache without fetching…
        await query.Execute(parameters);
        Assert.Equal(1, calls);

        // …but Refetch forces the fetch and updates the shared entry.
        await query.Refetch();

        Assert.Equal(2, calls);
        Assert.Equal(2, query.Data);
        Assert.Equal(2, client.GetData<int>(QueryKey.Of("todos")));
    }

    [Fact]
    public async Task Refetch_Cached_UpdatesEveryComponentOnTheKey()
    {
        using var client = CreateClient();
        using var first = new Query<int>(client);
        using var second = new Query<int>(client);
        var calls = 0;

        await first.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => Task.FromResult(++calls),
        });
        await second.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => Task.FromResult(++calls),
        });

        await first.Refetch();

        Assert.Equal(2, calls);
        Assert.Equal(2, first.Data);
        Assert.Equal(2, second.Data);
    }

    [Fact]
    public async Task Refetch_BypassesEnabledFalse()
    {
        using var client = CreateClient();
        using var query = new Query<int>(client);
        var calls = 0;

        await query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => Task.FromResult(++calls),
            Enabled = false,
        });
        Assert.Equal(0, calls);

        await query.Refetch();

        Assert.Equal(1, calls);
        Assert.Equal(1, query.Data);
    }

    [Fact]
    public async Task Refetch_WithOverrides_ReplacesCallbacksForThatCallOnly()
    {
        using var query = new Query<int>();
        var executeSuccesses = 0;
        var refetchSuccesses = 0;

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = _ => Task.FromResult(1),
            OnSuccess = _ => executeSuccesses++,
        });
        Assert.Equal(1, executeSuccesses);

        await query.Refetch(new RefetchParameters<int> { OnSuccess = _ => refetchSuccesses++ });

        Assert.Equal(1, executeSuccesses);
        Assert.Equal(1, refetchSuccesses);

        // One-shot: a later bare Refetch runs the last Execute's callback again.
        await query.Refetch();

        Assert.Equal(2, executeSuccesses);
        Assert.Equal(1, refetchSuccesses);
    }

    [Fact]
    public async Task Refetch_WithNullOverrides_KeepsRememberedValues()
    {
        using var query = new Query<int>();
        var successes = 0;

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = _ => Task.FromResult(1),
            OnSuccess = _ => successes++,
        });

        await query.Refetch(new RefetchParameters<int>());

        Assert.Equal(2, successes);
    }

    [Fact]
    public async Task Refetch_WithErrorOverride_ObservesTheFailure()
    {
        using var query = new Query<int>();
        var shouldFail = false;
        Exception? observed = null;

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = _ => shouldFail
                ? Task.FromException<int>(new InvalidDataException("boom"))
                : Task.FromResult(1),
        });

        shouldFail = true;
        await query.Refetch(new RefetchParameters<int> { OnError = e => observed = e });

        Assert.IsType<InvalidDataException>(observed);
        Assert.True(query.IsError);
    }

    [Fact]
    public async Task Refetch_WithCancellationTokenOverride_CancelsTheRefetch()
    {
        using var query = new Query<int>();
        using var cts = new CancellationTokenSource();
        var completion = new TaskCompletionSource<int>();
        var firstCall = true;

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = _ =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return Task.FromResult(1);
                }

                return completion.Task;
            },
        });

        var refetch = query.Refetch(new RefetchParameters<int>
        {
            OnSuccess = _ => Assert.Fail("A cancelled refetch must not run success callbacks."),
            CancellationToken = cts.Token,
        });

        await cts.CancelAsync();
        completion.SetResult(2);
        await refetch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, query.Data);
    }

    [Fact]
    public async Task Refetch_JoinsAnInFlightSharedFetch()
    {
        using var client = CreateClient();
        using var query = new Query<int>(client);
        var completion = new TaskCompletionSource<int>();
        var calls = 0;

        await query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => Task.FromResult(0),
        });

        await client.InvalidateAsync(QueryKey.Of("todos"), new InvalidateOptions { RefetchType = RefetchType.None });

        var slow = query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => { calls++; return completion.Task; },
        });

        var refetch = query.Refetch();

        completion.SetResult(5);
        await Task.WhenAll(slow, refetch).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, calls);
        Assert.Equal(5, query.Data);
    }
}
