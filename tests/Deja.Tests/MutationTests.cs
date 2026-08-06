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
    public async Task PropertyChanged_IsRaisedForLoadingAndData()
    {
        var mutation = new Mutation<int>();
        var changed = new List<string?>();
        mutation.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await mutation.Execute(new MutationParameters<int> { MutationFunction = () => Task.FromResult(1) });

        Assert.Contains(nameof(Mutation<int>.IsLoading), changed);
        Assert.Contains(nameof(Mutation<int>.Data), changed);
    }
}
