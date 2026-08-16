namespace Deja.Tests;

public class QueryTests
{
    [Fact]
    public async Task Execute_SetsData_AndTogglesLoadingFlags()
    {
        using var query = new Query<int>();
        var tcs = new TaskCompletionSource<int>();

        var execution = query.Execute(new QueryParameters<int> { QueryFunction = _ => tcs.Task });

        Assert.True(query.IsLoading);
        Assert.False(query.IsReFetching);

        tcs.SetResult(42);
        await execution;

        Assert.Equal(42, query.Data);
        Assert.False(query.IsLoading);
        Assert.False(query.IsError);
        Assert.Equal(1, query.ReFetchCount);
    }

    [Fact]
    public async Task Execute_SecondRun_ReportsReFetching()
    {
        using var query = new Query<int>();
        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(1) });

        var tcs = new TaskCompletionSource<int>();
        var execution = query.Execute(new QueryParameters<int> { QueryFunction = _ => tcs.Task });

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
                QueryFunction = _ => Task.FromException<int>(new InvalidDataException("boom")),
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
            QueryFunction = _ => Task.FromException<int>(new InvalidDataException("boom")),
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
            QueryFunction = _ => Task.FromException<int>(new DisplayUserException("user-facing", "internal")),
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
            QueryFunction = _ => Task.FromException<int>(new InvalidDataException("boom")),
            OnError = _ => { },
        });
        Assert.True(query.IsError);

        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(7) });

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

        Func<CancellationToken, Task<int>> fetch = _ =>
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

        var first = query.Execute(new QueryParameters<string> { QueryFunction = _ => slow.Task });
        var second = query.Execute(new QueryParameters<string> { QueryFunction = _ => Task.FromResult("fresh") });

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
            QueryFunction = async token =>
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
        var second = query.Execute(new QueryParameters<string> { QueryFunction = _ => Task.FromResult("fresh") });

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
            QueryFunction = _ =>
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
            QueryFunction = _ => Task.FromResult(1),
            OnSettled = _ => settled++,
        });
        Assert.Equal(1, settled);

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = _ => Task.FromException<int>(new InvalidDataException("boom")),
            OnError = _ => { },
            OnSettled = _ => settled++,
        });
        Assert.Equal(2, settled);

        var slow = new TaskCompletionSource<int>();
        var superseded = query.Execute(new QueryParameters<int>
        {
            QueryFunction = _ => slow.Task,
            OnSettled = _ => settled++,
        });
        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = _ => Task.FromResult(2),
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
        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(3) });

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
            QueryFunction = async token =>
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
        await query.Execute(new QueryParameters<string> { QueryFunction = _ => Task.FromResult("late") });
        Assert.Null(query.Data);
    }

    [Fact]
    public async Task Execute_NotifiesOncePerStateTransition()
    {
        using var query = new Query<int>();
        var notifications = 0;
        using var attachment = query.Attach(() => notifications++);

        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(1) });

        // Entry (IsLoading), then data and cleared flags together. Publishing data separately from
        // clearing the flags would only be observable to a success callback, and there is none.
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task Execute_WithSuccessCallback_PublishesDataBeforeTheCallbackRuns()
    {
        using var query = new Query<int>();
        var notifications = 0;
        var dataAtCallback = 0;
        var notificationsAtCallback = 0;
        using var attachment = query.Attach(() => notifications++);

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = _ => Task.FromResult(7),
            OnSuccess = _ =>
            {
                dataAtCallback = query.Data;
                notificationsAtCallback = notifications;
            },
        });

        // The extra notification is what a callback buys: the owner has already rendered the data
        // by the time the callback runs, so an OnSuccess that touches other state can't be
        // sequenced behind a render showing stale data.
        Assert.Equal(3, notifications);
        Assert.Equal(7, dataAtCallback);
        Assert.Equal(2, notificationsAtCallback);
    }

    [Fact]
    public async Task Execute_NotifiesWithCoherentState()
    {
        using var query = new Query<int>();
        var snapshots = new List<(bool Loading, int? Data)>();
        using var attachment = query.Attach(() => snapshots.Add((query.IsLoading, query.Data)));

        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(7) });

        // The listener never observes a half-applied transition: loading is announced on its own,
        // then data arrives with the flags already cleared.
        Assert.Equal((true, 0), snapshots[0]);
        Assert.Equal((false, 7), snapshots[^1]);
    }

    [Fact]
    public async Task Execute_DoesNotNotifyForAnUnchangedTransition()
    {
        using var query = new Query<int>();
        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(1) });

        var notifications = 0;
        using var attachment = query.Attach(() => notifications++);

        // Same value, so only the loading flags move: on and off again, never a data render.
        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(1) });

        Assert.Equal(2, notifications);
        Assert.Equal(1, query.Data);
    }

    [Fact]
    public async Task Execute_ErrorPath_NotifiesOncePerTransition()
    {
        using var query = new Query<int>();
        var notifications = 0;
        using var attachment = query.Attach(() => notifications++);

        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = _ => Task.FromException<int>(new InvalidDataException("boom")),
            OnError = _ => { },
        });

        // Entry, error (IsError + ErrorMessage), exit. The error is published before the callback
        // runs, so unlike the success path this transition keeps its middle notification.
        Assert.Equal(3, notifications);
    }

    [Fact]
    public async Task ClearData_NotifiesForTheWholeReset()
    {
        using var query = new Query<int>();
        await query.Execute(new QueryParameters<int>
        {
            QueryFunction = _ => Task.FromException<int>(new InvalidDataException("boom")),
            OnError = _ => { },
        });
        Assert.True(query.IsError);

        var clearedError = false;
        using var attachment = query.Attach(() => clearedError = !query.IsError);

        query.ClearData();

        // Previously only Data was raised, so a component binding IsError never saw the reset.
        Assert.True(clearedError);
        Assert.False(query.IsError);
        Assert.Null(query.ErrorMessage);
    }

    [Fact]
    public void Attach_SecondListenerWhileOneIsLive_Throws()
    {
        using var query = new Query<int>();
        using var attachment = query.Attach(() => { });

        Assert.Throws<InvalidOperationException>(() => query.Attach(() => { }));
    }

    [Fact]
    public async Task Attach_AfterDetach_SlotIsFree_AndOldListenerIsSilent()
    {
        using var query = new Query<int>();
        var first = 0;
        var second = 0;

        var attachment = query.Attach(() => first++);
        attachment.Dispose();
        // Idempotent: disposing twice must not throw or free a slot it no longer owns.
        attachment.Dispose();

        using var reattached = query.Attach(() => second++);
        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(1) });

        Assert.Equal(0, first);
        Assert.True(second > 0);
    }

    [Fact]
    public async Task NotifyChanged_AfterDetach_IsNoOp()
    {
        using var query = new Query<int>();
        var notifications = 0;

        var attachment = query.Attach(() => notifications++);
        attachment.Dispose();

        await query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(1) });

        Assert.Equal(0, notifications);
        Assert.Equal(1, query.Data);
    }

    // Chaining is built on the callbacks, so they have to fire on every completed Execute — not
    // only the ones that reached the network. A fresh cache hit that stayed silent would stall
    // the chain with the parent's data already on screen.
    private static DejaClient CreateFreshCacheClient()
        => new(new DejaOptions
        {
            DefaultStaleTime = TimeSpan.FromMinutes(30),
            DefaultRefetchOnMount = RefetchOnMount.IfStale,
        });

    [Fact]
    public async Task Execute_CacheHitWithoutFetching_StillRunsSuccessAndSettledCallbacks()
    {
        var client = CreateFreshCacheClient();
        using var seed = new Query<int>(client);
        await seed.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("user"),
            QueryFunction = _ => Task.FromResult(7),
        });

        using var query = new Query<int>(client);
        var calls = 0;
        int? success = null;
        var settled = 0;

        await query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("user"),
            QueryFunction = _ => Task.FromResult(++calls),
            OnSuccess = data => success = data,
            OnSettled = _ => settled++,
        });

        Assert.Equal(0, calls);
        Assert.Equal(7, success);
        Assert.Equal(1, settled);
    }

    [Fact]
    public async Task Execute_CacheHit_ChainsADependentQueryFromOnSuccess()
    {
        var client = CreateFreshCacheClient();
        using var seed = new Query<int>(client);
        await seed.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("user"),
            QueryFunction = _ => Task.FromResult(42),
        });

        using var user = new Query<int>(client);
        using var orders = new Query<string>(client);

        await user.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("user"),
            QueryFunction = _ => Task.FromResult(0),
            OnSuccessAsync = async id => await orders.Execute(new QueryParameters<string>
            {
                QueryKey = QueryKey.Of("orders", id),
                QueryFunction = _ => Task.FromResult($"orders-{id}"),
            }),
        });

        // The chain ran off cached parent data, and the awaited Execute did not return until the
        // dependent query had finished.
        Assert.Equal("orders-42", orders.Data);
    }

    [Fact]
    public async Task Execute_DisabledWithEmptyCache_SettlesWithoutReportingSuccess()
    {
        var client = CreateFreshCacheClient();
        using var query = new Query<int>(client);
        var success = 0;
        var settled = 0;

        await query.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("user"),
            QueryFunction = _ => Task.FromResult(1),
            Enabled = false,
            OnSuccess = _ => success++,
            OnSettled = _ => settled++,
        });

        // Nothing was fetched and nothing was cached, so there is no result to chain from —
        // but the execution did finish.
        Assert.Equal(0, success);
        Assert.Equal(1, settled);
        Assert.Equal(0, query.Data);
    }
}
