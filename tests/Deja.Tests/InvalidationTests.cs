namespace Deja.Tests;

public class InvalidationTests
{
    // Long stale time everywhere: nothing refetches on its own, so every refetch observed in
    // these tests was caused by the invalidation under test.
    private static DejaClient CreateClient(TimeProvider? time = null)
        => new(new DejaOptions
        {
            DefaultStaleTime = TimeSpan.FromMinutes(30),
            TimeProvider = time ?? TimeProvider.System,
        });

    private static QueryParameters<int> Keyed(QueryKey key, Func<CancellationToken, Task<int>> fetch)
        => new() { QueryKey = key, QueryFunction = fetch };

    [Fact]
    public async Task ClearData_CancellingOverload_SupersedesAnInFlightCachedExecution()
    {
        using var client = CreateClient();
        var completion = new TaskCompletionSource<int>();
        var settled = 0;
        var succeeded = 0;

        using var query = new Query<int>(client);
        var execution = query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => completion.Task,
            OnSuccess = _ => succeeded++,
            OnSettled = _ => settled++,
        });

        // The cached path has no per-execution token, so before the generation was retired here
        // this reset was silently ignored: the fetch below still published its result and ran its
        // callbacks over the state the caller had just cleared.
        query.ClearData(cancelCurrentRequest: true);

        completion.SetResult(7);
        await execution.WaitAsync(TimeSpan.FromSeconds(5));

        // The superseded execution runs no callbacks of its own.
        Assert.Equal(0, succeeded);
        Assert.Equal(0, settled);

        // Data still arrives — but through the entry subscription, exactly as it would for any
        // other component on this key. ClearData resets this query's view; dropping the shared
        // entry is DejaClient.Remove's job.
        Assert.Equal(7, query.Data);
    }

    [Fact]
    public async Task InvalidateAsync_PrefixMatch_RefetchesEverySubscribedEntryUnderIt()
    {
        using var client = CreateClient();
        var listCalls = 0;
        var detailCalls = 0;

        using var list = new Query<int>(client);
        using var detail = new Query<int>(client);
        await list.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++listCalls)));
        await detail.Execute(Keyed(QueryKey.Of("todos", 5), _ => Task.FromResult(++detailCalls)));

        await client.InvalidateAsync(QueryKey.Of("todos"));

        Assert.Equal(2, listCalls);
        Assert.Equal(2, detailCalls);
        Assert.Equal(2, list.Data);
        Assert.Equal(2, detail.Data);
    }

    [Fact]
    public async Task InvalidateAsync_Exact_LeavesLongerKeysAlone()
    {
        using var client = CreateClient();
        var listCalls = 0;
        var detailCalls = 0;

        using var list = new Query<int>(client);
        using var detail = new Query<int>(client);
        await list.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++listCalls)));
        await detail.Execute(Keyed(QueryKey.Of("todos", 5), _ => Task.FromResult(++detailCalls)));

        await client.InvalidateAsync(QueryKey.Of("todos"), new InvalidateOptions { Exact = true });

        Assert.Equal(2, listCalls);
        Assert.Equal(1, detailCalls);
    }

    [Fact]
    public async Task InvalidateAsync_Predicate_NarrowsTheMatch()
    {
        using var client = CreateClient();
        var oddCalls = 0;
        var evenCalls = 0;

        using var odd = new Query<int>(client);
        using var even = new Query<int>(client);
        await odd.Execute(Keyed(QueryKey.Of("todos", 1), _ => Task.FromResult(++oddCalls)));
        await even.Execute(Keyed(QueryKey.Of("todos", 2), _ => Task.FromResult(++evenCalls)));

        await client.InvalidateAsync(QueryKey.Of("todos"), new InvalidateOptions
        {
            Predicate = static key => key.Segments is [_, int id] && id % 2 == 1,
        });

        Assert.Equal(2, oddCalls);
        Assert.Equal(1, evenCalls);
    }

    [Fact]
    public async Task InvalidateAsync_RefetchTypeNone_OnlyMarksStale()
    {
        using var client = CreateClient();
        var calls = 0;

        using var query = new Query<int>(client);
        await query.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));

        await client.InvalidateAsync(QueryKey.Of("todos"), new InvalidateOptions { RefetchType = RefetchType.None });

        Assert.Equal(1, calls);
        Assert.True(query.IsStale);
        Assert.True(client.GetState(QueryKey.Of("todos"))!.IsInvalidated);

        // The mark forces the next mount to refetch despite the long stale time.
        await query.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));
        Assert.Equal(2, calls);
        Assert.False(query.IsStale);
    }

    [Fact]
    public async Task InvalidateAsync_ZeroSubscriberEntry_DefersTheRefetchToTheNextMount()
    {
        using var client = CreateClient();
        var calls = 0;

        using (var mounted = new Query<int>(client))
        {
            await mounted.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));
        }

        // Active (the default) refetches only entries someone is looking at.
        await client.InvalidateAsync(QueryKey.Of("todos"));
        Assert.Equal(1, calls);

        using var remounted = new Query<int>(client);
        await remounted.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));

        Assert.Equal(2, calls);
        Assert.Equal(2, remounted.Data);
    }

    [Fact]
    public async Task InvalidateAsync_RefetchTypeAll_AlsoRefetchesUnsubscribedEntries()
    {
        using var client = CreateClient();
        var calls = 0;

        using (var mounted = new Query<int>(client))
        {
            await mounted.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));
        }

        await client.InvalidateAsync(QueryKey.Of("todos"), new InvalidateOptions { RefetchType = RefetchType.All });

        Assert.Equal(2, calls);
        Assert.Equal(2, client.GetData<int>(QueryKey.Of("todos")));
    }

    [Fact]
    public async Task RefetchAsync_RefetchesFreshEntries()
    {
        using var client = CreateClient();
        var calls = 0;

        using var query = new Query<int>(client);
        await query.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));

        await client.RefetchAsync(QueryKey.Of("todos"));

        Assert.Equal(2, calls);
        Assert.Equal(2, query.Data);
    }

    [Fact]
    public async Task SetData_UpdaterForm_TransformsTheCurrentValue_AndNotifiesSubscribers()
    {
        using var client = CreateClient();

        using var query = new Query<int>(client);
        await query.Execute(Keyed(QueryKey.Of("count"), _ => Task.FromResult(1)));

        client.SetData<int>(QueryKey.Of("count"), static current => current + 41);

        Assert.Equal(42, client.GetData<int>(QueryKey.Of("count")));
        Assert.Equal(42, query.Data);
    }

    [Fact]
    public void SetData_OnAnAbsentKey_CreatesTheEntry()
    {
        using var client = CreateClient();

        client.SetData(QueryKey.Of("count"), 5);
        client.SetData<int>(QueryKey.Of("fresh"), static current => current + 1);

        Assert.Equal(5, client.GetData<int>(QueryKey.Of("count")));
        Assert.Equal(1, client.GetData<int>(QueryKey.Of("fresh")));
    }

    [Fact]
    public async Task Remove_DropsTheEntry_SoTheNextExecuteFetchesFresh()
    {
        using var client = CreateClient();
        var calls = 0;

        using (var query = new Query<int>(client))
        {
            await query.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));
        }

        client.Remove(QueryKey.Of("todos"));
        Assert.Equal(0, client.EntryCount);

        using var fresh = new Query<int>(client);
        await fresh.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));

        Assert.Equal(2, calls);
        Assert.False(fresh.IsCachedData);
    }

    [Fact]
    public void Clear_DropsEveryEntry()
    {
        using var client = CreateClient();
        client.SetData(QueryKey.Of("a"), 1);
        client.SetData(QueryKey.Of("b"), 2);

        client.Clear();

        Assert.Equal(0, client.EntryCount);
        Assert.Null(client.GetState(QueryKey.Of("a")));
    }

    [Fact]
    public async Task SetDefaults_LongestMatchingPrefixWins_RegardlessOfRegistrationOrder()
    {
        using var client = new DejaClient();
        var detailCalls = 0;
        var listCalls = 0;

        // Specific registered BEFORE generic: length ordering, not registration order, must win.
        client.SetDefaults(QueryKey.Of("todos", "detail"), new QueryDefaults { StaleTime = TimeSpan.FromMinutes(30) });
        client.SetDefaults(QueryKey.Of("todos"), new QueryDefaults { StaleTime = TimeSpan.Zero });

        using var detail = new Query<int>(client);
        await detail.Execute(Keyed(QueryKey.Of("todos", "detail", 1), _ => Task.FromResult(++detailCalls)));
        await detail.Execute(Keyed(QueryKey.Of("todos", "detail", 1), _ => Task.FromResult(++detailCalls)));

        // 30-minute stale time from the longer prefix: still fresh, no second fetch.
        Assert.Equal(1, detailCalls);

        using var list = new Query<int>(client);
        await list.Execute(Keyed(QueryKey.Of("todos", "list"), _ => Task.FromResult(++listCalls)));
        await list.Execute(Keyed(QueryKey.Of("todos", "list"), _ => Task.FromResult(++listCalls)));

        // Zero stale time from the shorter prefix: always refetch.
        Assert.Equal(2, listCalls);
    }

    [Fact]
    public async Task SetDefaults_PerQueryValue_BeatsEveryPrefix()
    {
        using var client = new DejaClient();
        var calls = 0;

        client.SetDefaults(QueryKey.Of("todos"), new QueryDefaults { StaleTime = TimeSpan.FromMinutes(30) });

        using var query = new Query<int>(client);
        var parameters = () => new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos", 1),
            QueryFunction = _ => Task.FromResult(++calls),
            StaleTime = TimeSpan.Zero,
        };

        await query.Execute(parameters());
        await query.Execute(parameters());

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SetDefaults_UnsetProperty_FallsThroughToAShorterPrefix()
    {
        using var client = new DejaClient();
        var calls = 0;

        // The longer prefix sets only RefetchOnMount; StaleTime must come from the shorter one.
        client.SetDefaults(QueryKey.Of("todos"), new QueryDefaults { StaleTime = TimeSpan.FromMinutes(30) });
        client.SetDefaults(QueryKey.Of("todos", "detail"), new QueryDefaults { RefetchOnMount = RefetchOnMount.IfStale });

        using var query = new Query<int>(client);
        await query.Execute(Keyed(QueryKey.Of("todos", "detail", 1), _ => Task.FromResult(++calls)));
        await query.Execute(Keyed(QueryKey.Of("todos", "detail", 1), _ => Task.FromResult(++calls)));

        Assert.Equal(1, calls);
    }
}
