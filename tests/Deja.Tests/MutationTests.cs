namespace Deja.Tests;

public class MutationTests
{
    [Fact]
    public async Task Execute_SetsData_AndRunsSuccessCallbacks()
    {
        var mutation = new Mutation<int>();
        int? syncResult = null;
        int? asyncResult = null;

        await mutation.Execute(new MutationParameters<int>
        {
            MutationFunction = () => Task.FromResult(42),
            OnSuccess = d => syncResult = d,
            OnSuccessAsync = d =>
            {
                asyncResult = d;
                return Task.CompletedTask;
            },
        });

        Assert.Equal(42, mutation.Data);
        Assert.Equal(42, syncResult);
        Assert.Equal(42, asyncResult);
        Assert.False(mutation.IsLoading);
        Assert.False(mutation.IsError);
    }

    [Fact]
    public async Task Execute_VoidMutation_Succeeds()
    {
        var mutation = new Mutation<object>();
        var ran = false;

        await mutation.Execute(new MutationParameters<object>
        {
            VoidMutationFunction = () =>
            {
                ran = true;
                return Task.CompletedTask;
            },
        });

        Assert.True(ran);
        Assert.False(mutation.IsError);
    }

    [Fact]
    public async Task Execute_WithoutAnyFunction_Throws()
    {
        var mutation = new Mutation<int>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutation.Execute(new MutationParameters<int>()));

        Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.True(mutation.IsError);
    }

    [Fact]
    public async Task Execute_OnFailure_RunsErrorCallbacks_AndRethrowsWrapped()
    {
        var mutation = new Mutation<int>();
        Exception? observed = null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutation.Execute(new MutationParameters<int>
            {
                MutationFunction = () => Task.FromException<int>(new InvalidDataException("boom")),
                OnError = e => observed = e,
            }));

        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.IsType<InvalidDataException>(observed);
        Assert.True(mutation.IsError);
        Assert.Equal("boom", mutation.ErrorMessage);
        Assert.Equal(0, mutation.Data);
    }

    [Fact]
    public async Task Execute_DisplayUserException_RoutesToDisplayCallbacks()
    {
        var mutation = new Mutation<int>();
        string? displayMessage = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutation.Execute(new MutationParameters<int>
            {
                MutationFunction = () => Task.FromException<int>(new DisplayUserException("user-facing")),
                OnDisplayUserError = e => displayMessage = e.DisplayMessage,
            }));

        Assert.Equal("user-facing", displayMessage);
    }

    [Fact]
    public async Task Execute_SettledCallback_RunsOnSuccessAndFailure()
    {
        var mutation = new Mutation<int>();
        var settled = 0;

        await mutation.Execute(new MutationParameters<int>
        {
            MutationFunction = () => Task.FromResult(1),
            OnSettled = _ => settled++,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutation.Execute(new MutationParameters<int>
            {
                MutationFunction = () => Task.FromException<int>(new InvalidDataException("boom")),
                OnSettled = _ => settled++,
            }));

        Assert.Equal(2, settled);
    }

    [Fact]
    public async Task Execute_FailureAfterSuccess_ResetsData()
    {
        var mutation = new Mutation<int>();

        await mutation.Execute(new MutationParameters<int> { MutationFunction = () => Task.FromResult(42) });
        Assert.Equal(42, mutation.Data);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutation.Execute(new MutationParameters<int>
            {
                MutationFunction = () => Task.FromException<int>(new InvalidDataException("boom")),
            }));

        Assert.Equal(0, mutation.Data);
    }

    [Fact]
    public async Task Execute_NotifiesOncePerStateTransition()
    {
        var mutation = new Mutation<int>();
        var notifications = 0;
        using var attachment = mutation.Attach(() => notifications++);

        await mutation.Execute(new MutationParameters<int> { MutationFunction = () => Task.FromResult(1) });

        // Entry (cleared error + IsLoading), success (Data), exit (IsLoading cleared).
        Assert.Equal(3, notifications);
    }

    [Fact]
    public async Task Execute_NotifiesWithCoherentState()
    {
        var mutation = new Mutation<int>();
        var snapshots = new List<(bool Loading, int? Data)>();
        using var attachment = mutation.Attach(() => snapshots.Add((mutation.IsLoading, mutation.Data)));

        await mutation.Execute(new MutationParameters<int> { MutationFunction = () => Task.FromResult(7) });

        Assert.Equal((true, 0), snapshots[0]);
        Assert.Equal((true, 7), snapshots[1]);
        Assert.Equal((false, 7), snapshots[^1]);
    }

    [Fact]
    public async Task Execute_ErrorPath_NotifiesOncePerTransition()
    {
        var mutation = new Mutation<int>();
        var notifications = 0;
        using var attachment = mutation.Attach(() => notifications++);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutation.Execute(new MutationParameters<int>
            {
                MutationFunction = () => Task.FromException<int>(new InvalidDataException("boom")),
                OnError = _ => { },
            }));

        // Entry, error (IsError + ErrorMessage + cleared Data), exit.
        Assert.Equal(3, notifications);
    }

    [Fact]
    public void Attach_SecondListenerWhileOneIsLive_Throws()
    {
        var mutation = new Mutation<int>();
        using var attachment = mutation.Attach(() => { });

        Assert.Throws<InvalidOperationException>(() => mutation.Attach(() => { }));
    }

    [Fact]
    public async Task NotifyChanged_AfterDetach_IsNoOp()
    {
        var mutation = new Mutation<int>();
        var notifications = 0;

        var attachment = mutation.Attach(() => notifications++);
        attachment.Dispose();

        await mutation.Execute(new MutationParameters<int> { MutationFunction = () => Task.FromResult(1) });

        Assert.Equal(0, notifications);
        Assert.Equal(1, mutation.Data);
    }
}
