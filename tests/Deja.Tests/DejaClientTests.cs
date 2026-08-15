namespace Deja.Tests;

public class DejaClientTests
{
    private static DejaClient CreateClient(
        TimeSpan? staleTime = null,
        TimeProvider? time = null,
        Action<DejaOptions>? configure = null)
    {
        var options = new DejaOptions
        {
            DefaultStaleTime = staleTime ?? TimeSpan.Zero,
            TimeProvider = time ?? TimeProvider.System,
        };

        configure?.Invoke(options);
        return new DejaClient(options);
    }

    [Fact]
    public async Task CachedExecute_SecondMount_ServesFromCache_WithoutFetching()
    {
        using var client = CreateClient(staleTime: TimeSpan.FromMinutes(5));
        var calls = 0;

        using (var first = new Query<int>(client))
        {
            await first.Execute(new QueryParameters<int>
            {
                QueryKey = QueryKey.Of("todos"),
                QueryFunction = _ => Task.FromResult(++calls),
            });
        }

        // Remounting within the cache window: instant data, no request.
        using var second = new Query<int>(client);
        await second.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => Task.FromResult(++calls),
        });

        Assert.Equal(1, calls);
        Assert.Equal(1, second.Data);
        Assert.True(second.IsCachedData);
        Assert.False(second.IsLoading);
        Assert.NotNull(second.UpdatedAt);
    }

    [Fact]
    public async Task CachedExecute_StaleData_RefetchesInBackground_KeepingOldDataOnScreen()
    {
        // DefaultStaleTime = Zero: cached data renders instantly but is always revalidated.
        using var client = CreateClient();

        using (var first = new Query<int>(client))
        {
            await first.Execute(new QueryParameters<int>
            {
                QueryKey = QueryKey.Of("todos"),
                QueryFunction = _ => Task.FromResult(1),
            });
        }

        var refetch = new TaskCompletionSource<int>();
        using var second = new Query<int>(client);
        var execution = second.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => refetch.Task,
        });

        // Old data stays on screen while the background refetch runs — no loading flash.
        Assert.Equal(1, second.Data);
        Assert.True(second.IsCachedData);
        Assert.True(second.IsReFetching);

        refetch.SetResult(2);
        await execution;

        Assert.Equal(2, second.Data);
        Assert.False(second.IsCachedData);
        Assert.False(second.IsReFetching);
    }

    [Fact]
    public async Task CachedExecute_FreshData_DoesNotRefetch()
    {
        using var client = CreateClient(staleTime: TimeSpan.FromMinutes(5));
        var calls = 0;

        Func<CancellationToken, Task<int>> fetch = _ => Task.FromResult(++calls);
        var parameters = () => new QueryParameters<int> { QueryKey = QueryKey.Of("k"), QueryFunction = fetch };

        using var query = new Query<int>(client);
        await query.Execute(parameters());
        await query.Execute(parameters());

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CachedExecute_TwoQueriesOnOneKey_ShareOneFetch()
    {
        using var client = CreateClient();
        var calls = 0;
        var tcs = new TaskCompletionSource<int>();

        Func<CancellationToken, Task<int>> fetch = _ =>
        {
            calls++;
            return tcs.Task;
        };

        using var queryA = new Query<int>(client);
        using var queryB = new Query<int>(client);

        var executionA = queryA.Execute(new QueryParameters<int> { QueryKey = QueryKey.Of("k"), QueryFunction = fetch });
        var executionB = queryB.Execute(new QueryParameters<int> { QueryKey = QueryKey.Of("k"), QueryFunction = fetch });

        Assert.True(queryA.IsLoading);
        Assert.True(queryB.IsLoading);

        tcs.SetResult(42);
        await Task.WhenAll(executionA, executionB);

        Assert.Equal(1, calls);
        Assert.Equal(42, queryA.Data);
        Assert.Equal(42, queryB.Data);
    }

    [Fact]
    public async Task CachedExecute_EntryChange_UpdatesEverySubscribedQuery()
    {
        using var client = CreateClient();

        using var passive = new Query<int>(client);
        await passive.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => Task.FromResult(1),
        });

        var passiveNotified = 0;
        using var attachment = passive.Attach(() => passiveNotified++);

        // A different component's query refetches the same key...
        using var active = new Query<int>(client);
        await active.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => Task.FromResult(2),
        });

        // ...and the passive query updates through the entry, not through the other query.
        Assert.Equal(2, passive.Data);
        Assert.True(passiveNotified > 0);
        Assert.False(passive.IsCachedData);
    }

    [Fact]
    public async Task CachedExecute_DoesNotNotifyTwiceForTheSameTransition()
    {
        using var client = CreateClient();
        using var query = new Query<int>(client);

        var snapshots = new List<(bool Loading, int Data, bool Cached)>();
        using var attachment = query.Attach(
            () => snapshots.Add((query.IsLoading, query.Data, query.IsCachedData)));

        await query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => Task.FromResult(1),
        });

        // Execute and the shared entry both announce the fetch starting, and both announce it
        // finishing; each pair is one transition and must reach the component once. Asserting on
        // distinct snapshots rather than a bare count keeps this about the duplicates, not about
        // how many transitions the cached path happens to have.
        Assert.Equal(snapshots.Distinct().Count(), snapshots.Count);
        Assert.Equal((false, 1, false), snapshots[^1]);
    }

    [Fact]
    public async Task CachedExecute_PassiveSubscriber_IsNotNotifiedTwiceForOneRefetch()
    {
        using var client = CreateClient();

        using var passive = new Query<int>(client);
        await passive.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => Task.FromResult(1),
        });

        var snapshots = new List<(bool Loading, int Data)>();
        using var attachment = passive.Attach(() => snapshots.Add((passive.IsLoading, passive.Data)));

        using var active = new Query<int>(client);
        await active.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => Task.FromResult(2),
        });

        // A component showing shared data re-renders for what changed on the entry, not once per
        // notification the entry emits.
        Assert.Equal(snapshots.Distinct().Count(), snapshots.Count);
        Assert.Equal((false, 2), snapshots[^1]);
    }

    [Fact]
    public async Task CachedExecute_Callbacks_RunPerCaller_NotForPassiveUpdates()
    {
        using var client = CreateClient();
        var tcs = new TaskCompletionSource<int>();
        var successA = 0;
        var successB = 0;

        using var queryA = new Query<int>(client);
        using var queryB = new Query<int>(client);

        var executionA = queryA.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => tcs.Task,
            OnSuccess = _ => successA++,
        });
        var executionB = queryB.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => tcs.Task,
            OnSuccess = _ => successB++,
        });

        tcs.SetResult(1);
        await Task.WhenAll(executionA, executionB);

        // Both the initiator and the joiner ran their own callbacks.
        Assert.Equal(1, successA);
        Assert.Equal(1, successB);

        // A passive update (another query refetching the key) runs no callbacks on A or B.
        using var third = new Query<int>(client);
        await third.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => Task.FromResult(2),
        });

        Assert.Equal(2, queryA.Data);
        Assert.Equal(1, successA);
        Assert.Equal(1, successB);
    }

    [Fact]
    public async Task CachedExecute_CallerToken_DetachesCaller_WithoutCancellingSharedFetch()
    {
        using var client = CreateClient();
        var tcs = new TaskCompletionSource<int>();
        var fetchTokenCancelled = false;

        using var callerCts = new CancellationTokenSource();
        using var queryA = new Query<int>(client);
        using var queryB = new Query<int>(client);

        var executionA = queryA.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = token =>
            {
                token.Register(() => fetchTokenCancelled = true);
                return tcs.Task;
            },
            CancellationToken = callerCts.Token,
        });
        var executionB = queryB.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => tcs.Task,
        });

        // A's component goes away: A detaches, but the shared fetch keeps running for B.
        await callerCts.CancelAsync();
        await executionA;

        Assert.False(fetchTokenCancelled);

        tcs.SetResult(7);
        await executionB;

        Assert.Equal(7, queryB.Data);
    }

    [Fact]
    public async Task CachedExecute_ErrorPath_RecordsOnEntry_AndRunsEachCallersErrorCallbacks()
    {
        using var client = CreateClient();
        var tcs = new TaskCompletionSource<int>();
        var errorsA = 0;
        var errorsB = 0;

        using var queryA = new Query<int>(client);
        using var queryB = new Query<int>(client);

        var executionA = queryA.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => tcs.Task,
            OnError = _ => errorsA++,
        });
        var executionB = queryB.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => tcs.Task,
            OnError = _ => errorsB++,
        });

        tcs.SetException(new InvalidDataException("boom"));
        await Task.WhenAll(executionA, executionB);

        Assert.Equal(1, errorsA);
        Assert.Equal(1, errorsB);
        Assert.True(queryA.IsError);
        Assert.True(queryB.IsError);
        Assert.Equal("boom", queryA.ErrorMessage);
        Assert.Equal("boom", client.GetState(QueryKey.Of("k"))!.ErrorMessage);
    }

    [Fact]
    public async Task CachedExecute_UnhandledError_WrapsAndThrows_LikeTheUncachedPath()
    {
        using var client = CreateClient();
        using var query = new Query<int>(client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            query.Execute(new QueryParameters<int>
            {
                QueryKey = QueryKey.Of("k"),
                QueryFunction = _ => Task.FromException<int>(new InvalidDataException("boom")),
            }));

        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.True(query.IsError);
    }

    [Fact]
    public async Task UnkeyedExecute_NeverTouchesTheCache()
    {
        using var client = CreateClient();
        using var query = new Query<int>(client);

        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(1) });

        Assert.Equal(1, query.Data);
        Assert.Equal(0, client.EntryCount);
    }

    [Fact]
    public async Task CachedExecute_PlaceholderData_RendersWhileFirstFetchRuns_AndIsNeverCached()
    {
        using var client = CreateClient();
        var tcs = new TaskCompletionSource<int>();
        using var query = new Query<int>(client);

        var execution = query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => tcs.Task,
            PlaceholderData = 99,
        });

        Assert.Equal(99, query.Data);
        Assert.True(query.IsLoading);
        Assert.False(client.GetState(QueryKey.Of("k"))!.HasData);

        tcs.SetResult(1);
        await execution;

        Assert.Equal(1, query.Data);
    }

    [Fact]
    public async Task CachedExecute_Disabled_ServesCache_ButNeverFetches()
    {
        using var client = CreateClient();
        client.SetData(QueryKey.Of("k"), 5);

        var calls = 0;
        using var query = new Query<int>(client);
        await query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => Task.FromResult(++calls),
            Enabled = false,
        });

        Assert.Equal(5, query.Data);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CachedExecute_Select_TransformsPerQuery_CacheKeepsRawValue()
    {
        using var client = CreateClient(staleTime: TimeSpan.FromMinutes(5));
        using var query = new Query<int>(client);

        await query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => Task.FromResult(10),
            Select = value => value * 2,
        });

        Assert.Equal(20, query.Data);
        Assert.Equal(10, client.GetData<int>(QueryKey.Of("k")));
    }

    [Fact]
    public async Task CachedExecute_KeyChange_DetachesFromOldEntry()
    {
        using var client = CreateClient(staleTime: TimeSpan.FromMinutes(5));
        var slow = new TaskCompletionSource<int>();

        using var query = new Query<int>(client);
        var first = query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("a"),
            QueryFunction = _ => slow.Task,
        });

        await query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("b"),
            QueryFunction = _ => Task.FromResult(2),
        });

        // The old key's late result updates the old entry, not this query.
        slow.SetResult(1);
        await first;

        Assert.Equal(2, query.Data);
        Assert.False(query.IsLoading);
    }

    [Fact]
    public async Task CachedExecute_TypeMismatchOnKey_ThrowsLoudly()
    {
        using var client = CreateClient();
        client.SetData(QueryKey.Of("k"), 5);

        using var query = new Query<string>(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            query.Execute(new QueryParameters<string>
            {
                QueryKey = QueryKey.Of("k"),
                QueryFunction = _ => Task.FromResult("s"),
            }));
    }

    [Fact]
    public async Task KeyedExecute_WithoutClient_KeepsPreCacheJoinSemantics()
    {
        // No AddDeja anywhere: a keyed query still joins concurrent same-instance calls and no
        // cache is involved — exactly the pre-cache behavior.
        using var query = new Query<int>();
        var calls = 0;
        var tcs = new TaskCompletionSource<int>();

        Func<CancellationToken, Task<int>> fetch = _ =>
        {
            calls++;
            return tcs.Task;
        };

        var first = query.Execute(new QueryParameters<int> { QueryKey = QueryKey.Of("k"), QueryFunction = fetch });
        var second = query.Execute(new QueryParameters<int> { QueryKey = QueryKey.Of("k"), QueryFunction = fetch });

        tcs.SetResult(42);
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(42, query.Data);
    }

    [Fact]
    public void GetData_WrongType_ReturnsDefault_InsteadOfThrowing()
    {
        using var client = CreateClient();
        client.SetData(QueryKey.Of("k"), 5);

        Assert.Null(client.GetData<string>(QueryKey.Of("k")));
        Assert.False(client.TryGetData<string>(QueryKey.Of("k"), out _));
    }

    [Fact]
    public async Task StructuralComparison_EqualRefetch_SkipsNotification()
    {
        using var client = CreateClient(configure: static options => options.StructuralComparison = true);

        using var query = new Query<int>(client);
        await query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => Task.FromResult(5),
        });

        var dataNotifications = 0;
        var last = query.Data;
        using var attachment = query.Attach(() =>
        {
            if (query.Data != last)
            {
                last = query.Data;
                dataNotifications++;
            }
        });

        // Stale (zero stale time), so this refetches — to an equal value.
        await query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("k"),
            QueryFunction = _ => Task.FromResult(5),
        });

        Assert.Equal(0, dataNotifications);
        Assert.Equal(5, query.Data);
    }
}
