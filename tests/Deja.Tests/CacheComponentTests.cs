#if NET10_0_OR_GREATER
using Bunit;
using Microsoft.AspNetCore.Components.Rendering;

namespace Deja.Tests;

/// <summary>
/// End-to-end wiring through the component base: <c>AddDeja()</c> in DI, the base resolving the
/// client via its injected service provider, and discovered queries receiving it — plus the
/// opt-in guarantee that a container without <c>AddDeja()</c> leaves everything uncached.
/// </summary>
public class CacheComponentTests
{
    private sealed class CachedProbe : DejaComponentBase
    {
        public readonly Query<int> Todos = new();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
            => builder.AddContent(0, Todos.Data);
    }

    [Fact]
    public async Task AddDeja_HandsTheScopedClientToDiscoveredQueries()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddDeja(static options => options.DefaultStaleTime = TimeSpan.FromMinutes(5));

        var first = ctx.Render<CachedProbe>().Instance;
        await first.Todos.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => Task.FromResult(41),
        });

        // A second component on the same key gets the cached data without fetching: the client
        // really is shared through DI, not per component.
        var calls = 0;
        var second = ctx.Render<CachedProbe>().Instance;
        await second.Todos.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => Task.FromResult(++calls),
        });

        Assert.Equal(0, calls);
        Assert.Equal(41, second.Todos.Data);
        Assert.True(second.Todos.IsCachedData);
    }

    [Fact]
    public async Task WithoutAddDeja_ComponentsWork_FullyUncached()
    {
        using var ctx = new BunitContext();

        var probe = ctx.Render<CachedProbe>().Instance;
        await probe.Todos.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => Task.FromResult(1),
        });

        Assert.Equal(1, probe.Todos.Data);

        // No client resolved: a fresh component fetches again instead of hitting a cache.
        var calls = 0;
        var second = ctx.Render<CachedProbe>().Instance;
        await second.Todos.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = _ => Task.FromResult(10 + ++calls),
        });

        Assert.Equal(1, calls);
        Assert.Equal(11, second.Todos.Data);
    }

    [Fact]
    public async Task TwoRenderedComponents_OnOneKey_BothReRenderWhenTheEntryChanges()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddDeja();

        var componentA = ctx.Render<CachedProbe>();
        var componentB = ctx.Render<CachedProbe>();

        Func<CancellationToken, Task<int>> fetch = _ => Task.FromResult(7);
        var parameters = () => new QueryParameters<int> { QueryKey = QueryKey.Of("todos"), QueryFunction = fetch };

        await componentA.Instance.Todos.Execute(parameters());
        await componentB.Instance.Todos.Execute(parameters());

        // B refetches to a new value; A's markup follows through the shared entry.
        fetch = _ => Task.FromResult(8);
        await componentB.Instance.Todos.Execute(new QueryParameters<int>
        {
            QueryKey = QueryKey.Of("todos"),
            QueryFunction = fetch,
        });

        Assert.Equal(8, componentA.Instance.Todos.Data);
        componentA.WaitForAssertion(() => Assert.Equal("8", componentA.Markup));
    }
}
#endif
