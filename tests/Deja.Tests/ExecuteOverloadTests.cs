namespace Deja.Tests;

/// <summary>
/// The convenience <c>Execute</c> overloads are thin wrappers that build a parameters object and
/// delegate to the canonical overload, so these tests assert equivalence with the explicit form
/// rather than re-testing the execution semantics covered by <see cref="QueryTests"/>.
/// </summary>
public class ExecuteOverloadTests
{
    [Fact]
    public async Task Execute_WithoutKey_FetchesAndSetsData()
    {
        using var query = new Query<int>();

        await query.Execute(_ => Task.FromResult(42));

        Assert.Equal(42, query.Data);
        Assert.False(query.IsLoading);
        Assert.False(query.IsError);
        Assert.Equal(1, query.ReFetchCount);
    }

    [Fact]
    public async Task Execute_WithConfigure_AppliesCallbacks()
    {
        using var query = new Query<int>();
        var observed = 0;

        await query.Execute(_ => Task.FromResult(7), p => p.OnSuccess = data => observed = data);

        Assert.Equal(7, observed);
        Assert.Equal(7, query.Data);
    }

    [Fact]
    public async Task Execute_WithStringKey_UsesTheSharedCacheEntry()
    {
        using var client = new DejaClient();
        using var first = new Query<int>(client);
        using var second = new Query<int>(client);

        // The string converts implicitly to QueryKey.Of("counter"), so both queries observe one
        // entry: the second serves the first's cached data instead of fetching again.
        await first.Execute("counter", _ => Task.FromResult(1));
        await second.Execute("counter", _ => Task.FromResult(2), p => p.RefetchOnMount = RefetchOnMount.Never);

        Assert.Equal(1, second.Data);
        Assert.True(second.IsCachedData);
    }

    [Fact]
    public async Task Execute_WithNullKey_TakesTheUncachedPath()
    {
        using var client = new DejaClient();
        using var query = new Query<int>(client);

        await query.Execute(key: null, _ => Task.FromResult(5));

        Assert.Equal(5, query.Data);

        // Nothing was written to the cache: an unkeyed execution never creates an entry.
        Assert.Equal(0, client.EntryCount);
    }

    [Fact]
    public async Task Execute_ConfigureCanOverrideTheKeyedDefaults()
    {
        using var client = new DejaClient();
        using var query = new Query<int>(client);

        await query.Execute("todos", _ => Task.FromResult(1), p => p.StaleTime = TimeSpan.FromMinutes(5));

        // A fresh entry within its stale time is served from cache without refetching.
        var fetches = 0;
        await query.Execute("todos", _ =>
        {
            fetches++;
            return Task.FromResult(2);
        }, p => p.StaleTime = TimeSpan.FromMinutes(5));

        Assert.Equal(0, fetches);
        Assert.Equal(1, query.Data);
    }

    [Fact]
    public async Task Execute_NullQueryFunction_Throws()
    {
        using var query = new Query<int>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => query.Execute((Func<CancellationToken, Task<int>>)null!));
    }

    [Fact]
    public async Task MutationExecute_RunsAndSetsData()
    {
        var mutation = new Mutation<string>();

        await mutation.Execute(() => Task.FromResult("created"));

        Assert.Equal("created", mutation.Data);
        Assert.False(mutation.IsLoading);
        Assert.False(mutation.IsError);
    }

    [Fact]
    public async Task MutationExecute_WithConfigure_InvalidatesKeys()
    {
        using var client = new DejaClient();
        using var query = new Query<int>(client);
        var mutation = new Mutation<string>(client);

        var fetches = 0;
        await query.Execute("todos", _ =>
        {
            fetches++;
            return Task.FromResult(fetches);
        });

        Assert.Equal(1, fetches);

        // InvalidateKeys refetches the mounted query on that key, exactly as the explicit
        // MutationParameters form does.
        await mutation.Execute(() => Task.FromResult("ok"), p => p.InvalidateKeys = [QueryKey.Of("todos")]);

        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task MutationExecute_NullMutationFunction_Throws()
    {
        var mutation = new Mutation<string>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => mutation.Execute((Func<Task<string>>)null!));
    }
}
