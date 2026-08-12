namespace Deja.Tests;

public class EvictionTests
{
    // The sweep is driven directly (client.Sweep()) with a manual clock instead of waiting on the
    // eviction timer, so these tests never sleep.
    private static (DejaClient Client, TestTimeProvider Time) CreateClient(Action<DejaOptions>? configure = null)
    {
        var time = new TestTimeProvider();
        var options = new DejaOptions
        {
            DefaultStaleTime = TimeSpan.FromMinutes(30),
            TimeProvider = time,
        };

        configure?.Invoke(options);
        return (new DejaClient(options), time);
    }

    private static QueryParameters<int> Keyed(QueryKey key, Func<CancellationToken, Task<int>> fetch)
        => new() { QueryKey = key, QueryFunction = fetch };

    [Fact]
    public async Task Sweep_EvictsZeroSubscriberEntries_AfterCacheTime()
    {
        var (client, time) = CreateClient();
        using var _ = client;
        var calls = 0;

        using (var query = new Query<int>(client))
        {
            await query.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));
        }

        // Default cache time is 5 minutes; past it, the idle entry goes.
        time.Advance(TimeSpan.FromMinutes(6));
        client.Sweep();

        Assert.Equal(0, client.EntryCount);

        using var remounted = new Query<int>(client);
        await remounted.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));

        Assert.Equal(2, calls);
        Assert.False(remounted.IsCachedData);
    }

    [Fact]
    public async Task Sweep_LeavesIdleEntries_WithinCacheTime()
    {
        var (client, time) = CreateClient();
        using var _ = client;
        var calls = 0;

        using (var query = new Query<int>(client))
        {
            await query.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));
        }

        time.Advance(TimeSpan.FromMinutes(4));
        client.Sweep();

        Assert.Equal(1, client.EntryCount);

        // The fast back-navigation case the cache exists for: instant data, no request.
        using var remounted = new Query<int>(client);
        await remounted.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));

        Assert.Equal(1, calls);
        Assert.True(remounted.IsCachedData);
    }

    [Fact]
    public async Task Resubscribing_BeforeEvictAt_CancelsTheEviction()
    {
        var (client, time) = CreateClient();
        using var _ = client;
        var calls = 0;

        using (var query = new Query<int>(client))
        {
            await query.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));
        }

        time.Advance(TimeSpan.FromMinutes(4));

        // Remount within the window: the eviction timestamp is cleared while subscribed.
        using var remounted = new Query<int>(client);
        await remounted.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));

        time.Advance(TimeSpan.FromHours(2));
        client.Sweep();

        Assert.Equal(1, client.EntryCount);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Sweep_NeverEvictsASubscribedEntry()
    {
        var (client, time) = CreateClient();
        using var _ = client;

        using var query = new Query<int>(client);
        await query.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(1)));

        time.Advance(TimeSpan.FromDays(1));
        client.Sweep();

        Assert.Equal(1, client.EntryCount);
    }

    [Fact]
    public async Task PerQueryCacheTime_OverridesTheDefaultWindow()
    {
        var (client, time) = CreateClient();
        using var _ = client;

        using (var query = new Query<int>(client))
        {
            await query.Execute(new QueryParameters<int>
            {
                QueryKey = QueryKey.Of("short-lived"),
                QueryFunction = _ => Task.FromResult(1),
                CacheTime = TimeSpan.FromMinutes(1),
            });
        }

        time.Advance(TimeSpan.FromMinutes(2));
        client.Sweep();

        Assert.Equal(0, client.EntryCount);
    }

    [Fact]
    public void MaxEntries_EvictsLeastRecentlyUsedZeroSubscriberEntriesFirst()
    {
        var (client, time) = CreateClient(static options => options.MaxEntries = 2);
        using var _ = client;

        client.SetData(QueryKey.Of("a"), 1);
        time.Advance(TimeSpan.FromSeconds(1));
        client.SetData(QueryKey.Of("b"), 2);
        time.Advance(TimeSpan.FromSeconds(1));
        client.SetData(QueryKey.Of("c"), 3);

        Assert.Equal(2, client.EntryCount);
        Assert.Null(client.GetState(QueryKey.Of("a")));
        Assert.NotNull(client.GetState(QueryKey.Of("b")));
        Assert.NotNull(client.GetState(QueryKey.Of("c")));
    }

    [Fact]
    public async Task Mutation_InvalidateKeys_RefetchesEveryMountedQueryUnderThem()
    {
        var (client, _) = CreateClient();
        using var __ = client;
        var listCalls = 0;
        var detailCalls = 0;

        using var list = new Query<int>(client);
        using var detail = new Query<int>(client);
        await list.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++listCalls)));
        await detail.Execute(Keyed(QueryKey.Of("todos", 5), _ => Task.FromResult(++detailCalls)));

        var mutation = new Mutation<int>(client);
        await mutation.Execute(new MutationParameters<int>
        {
            MutationFunction = static () => Task.FromResult(0),
            InvalidateKeys = [QueryKey.Of("todos")],
        });

        // The prefix invalidation reached every mounted query, not just the mutating component's.
        Assert.Equal(2, listCalls);
        Assert.Equal(2, detailCalls);
        Assert.Equal(2, list.Data);
        Assert.Equal(2, detail.Data);
    }

    [Fact]
    public async Task Mutation_InvalidateKeys_RunAfterOnSuccess()
    {
        var (client, _) = CreateClient();
        using var __ = client;
        var order = new List<string>();

        using var query = new Query<int>(client);
        await query.Execute(Keyed(QueryKey.Of("todos"), _ =>
        {
            order.Add("fetch");
            return Task.FromResult(1);
        }));

        var mutation = new Mutation<int>(client);
        await mutation.Execute(new MutationParameters<int>
        {
            MutationFunction = static () => Task.FromResult(0),
            OnSuccess = _ => order.Add("success"),
            InvalidateKeys = [QueryKey.Of("todos")],
        });

        Assert.Equal(["fetch", "success", "fetch"], order);
    }

    [Fact]
    public async Task Mutation_InvalidateKeys_WithoutClient_IsIgnored()
    {
        var mutation = new Mutation<int>();

        await mutation.Execute(new MutationParameters<int>
        {
            MutationFunction = static () => Task.FromResult(7),
            InvalidateKeys = [QueryKey.Of("todos")],
        });

        Assert.Equal(7, mutation.Data);
    }

    [Fact]
    public async Task Mutation_FailedExecution_DoesNotInvalidate()
    {
        var (client, _) = CreateClient();
        using var __ = client;
        var calls = 0;

        using var query = new Query<int>(client);
        await query.Execute(Keyed(QueryKey.Of("todos"), _ => Task.FromResult(++calls)));

        var mutation = new Mutation<int>(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation.Execute(new MutationParameters<int>
        {
            MutationFunction = static () => Task.FromException<int>(new InvalidDataException("boom")),
            InvalidateKeys = [QueryKey.Of("todos")],
        }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LastSubscriberLeaving_CancelsTheSharedFetch()
    {
        var (client, _) = CreateClient();
        using var __ = client;
        var observedCancellation = new TaskCompletionSource();

        var query = new Query<int>(client);
        var execution = query.Execute(Keyed(QueryKey.Of("todos"), async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                observedCancellation.SetResult();
                throw;
            }

            return 0;
        }));

        // The only component looking at this key goes away: nothing is waiting on the result.
        query.Dispose();
        await execution;
        await observedCancellation.Task;

        Assert.Null(client.GetState(QueryKey.Of("todos"))!.ErrorMessage);
    }
}
