#if NET10_0_OR_GREATER
using Bunit;
using Microsoft.AspNetCore.Components.Rendering;

namespace Deja.Tests;

/// <summary>
/// The guarantee these cover: a derived component declares no <see cref="CancellationTokenSource"/>
/// and threads no token through its call sites, yet everything it started is cancelled when it is
/// disposed.
/// </summary>
public class ComponentLifetimeTests
{
    // The zero-boilerplate shape: state is declared, Execute is called with no token, and nothing
    // about cancellation appears anywhere in the component.
    private sealed class LifetimeProbe : DejaComponentBase
    {
        public readonly Query<int> Query = new();
        public readonly Mutation<int> Mutation = new();

        public CancellationToken CapturedToken { get; private set; }

        // ComponentToken is protected — this is the derived component reaching for its own token,
        // which is exactly how a consumer uses it.
        public CancellationToken ExposedToken => ComponentToken;

        public void CaptureToken() => CapturedToken = ComponentToken;

        public Task RunQuery(Func<CancellationToken, Task<int>> fetch) => Query.Execute(fetch);

        public Task RunKeyedQuery(QueryKey key, Func<CancellationToken, Task<int>> fetch)
            => Query.Execute(key, fetch, p => p.OnError = _ => { });

        public Task RunMutation(Func<CancellationToken, Task<int>> write) => Mutation.Execute(write);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
            => builder.AddContent(0, Query.Data);
    }

    // Cancellation must be observable from the derived cleanup hooks, which run before the base
    // detaches — that is what lets a component's own async cleanup bail out on disposal.
    private sealed class CleanupObservesTokenProbe : DejaComponentBase
    {
        public bool TokenCancelledInSyncDispose { get; private set; }

        public bool TokenCancelledInAsyncDispose { get; private set; }

        public void CaptureToken() => _ = ComponentToken;

        protected override void Dispose()
        {
            TokenCancelledInSyncDispose = ComponentToken.IsCancellationRequested;
            base.Dispose();
        }

        protected override ValueTask DisposeAsync()
        {
            TokenCancelledInAsyncDispose = ComponentToken.IsCancellationRequested;
            return base.DisposeAsync();
        }
    }

    [Fact]
    public async Task ThrowingCancellationCallback_DoesNotLeakAttachments()
    {
        using var ctx = new BunitContext();
        var rendered = ctx.Render<LifetimeProbe>();
        var component = rendered.Instance;

        // Cancelling runs every registration on the token, including consumer code the base does
        // not control. One throwing must not abort disposal and strand the state it owns.
        component.ExposedToken.Register(() => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAnyAsync<Exception>(async () => await component.DisposeComponentAsync());

        // The slot is free, so the query was detached despite the throw.
        using var reattached = component.Query.Attach(() => { });
    }

    [Fact]
    public void ComponentToken_IsNotCancelled_WhileMounted()
    {
        using var ctx = new BunitContext();
        var component = ctx.Render<LifetimeProbe>().Instance;

        component.CaptureToken();

        Assert.False(component.CapturedToken.IsCancellationRequested);
    }

    [Fact]
    public void ComponentToken_IsStable_AcrossReads()
    {
        using var ctx = new BunitContext();
        var component = ctx.Render<LifetimeProbe>().Instance;

        component.CaptureToken();
        var first = component.CapturedToken;
        component.CaptureToken();

        // One source per component: a fresh token each read would leave earlier registrations
        // attached to a source nothing ever cancels.
        Assert.Equal(first, component.CapturedToken);
    }

    [Fact]
    public async Task Disposal_CancelsComponentToken()
    {
        using var ctx = new BunitContext();
        var rendered = ctx.Render<LifetimeProbe>();
        var component = rendered.Instance;
        component.CaptureToken();

        await rendered.Instance.DisposeComponentAsync();

        Assert.True(component.CapturedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ComponentToken_ReadAfterDisposal_IsCancelled_NotThrowing()
    {
        using var ctx = new BunitContext();
        var rendered = ctx.Render<LifetimeProbe>();
        var component = rendered.Instance;

        await component.DisposeComponentAsync();

        // A late continuation reading the token must observe cancellation rather than an
        // ObjectDisposedException from the released source.
        component.CaptureToken();
        Assert.True(component.CapturedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Disposal_CancelsUnkeyedQueryFetch_WithNoTokenAtTheCallSite()
    {
        using var ctx = new BunitContext();
        var rendered = ctx.Render<LifetimeProbe>();
        var component = rendered.Instance;

        var started = new TaskCompletionSource();
        var fetchCancelled = false;
        var completion = new TaskCompletionSource<int>();

        var execution = component.RunQuery(token =>
        {
            token.Register(() => fetchCancelled = true);
            started.SetResult();
            return completion.Task;
        });

        await started.Task;
        await component.DisposeComponentAsync();

        Assert.True(fetchCancelled);

        // The cancelled execution completes quietly: no error state, nothing thrown at the caller.
        completion.SetCanceled();
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(component.Query.IsError);
    }

    [Fact]
    public async Task Disposal_CancelsMutation_WithNoTokenAtTheCallSite()
    {
        using var ctx = new BunitContext();
        var rendered = ctx.Render<LifetimeProbe>();
        var component = rendered.Instance;

        var started = new TaskCompletionSource();
        var writeCancelled = false;
        var completion = new TaskCompletionSource<int>();

        var execution = component.RunMutation(token =>
        {
            token.Register(() => writeCancelled = true);
            started.SetResult();
            return completion.Task;
        });

        await started.Task;
        await component.DisposeComponentAsync();

        Assert.True(writeCancelled);

        // Cancellation is not a failure: the mutation neither reports an error nor throws.
        completion.SetCanceled();
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(component.Mutation.IsError);
    }

    [Fact]
    public async Task Mutation_AfterDisposal_DoesNotStart()
    {
        using var ctx = new BunitContext();
        var rendered = ctx.Render<LifetimeProbe>();
        var component = rendered.Instance;

        await component.DisposeComponentAsync();

        var ran = false;
        await component.RunMutation(_ =>
        {
            ran = true;
            return Task.FromResult(1);
        });

        // A queued event handler firing after disposal must not start a write nobody can observe.
        Assert.False(ran);
        Assert.False(component.Mutation.IsLoading);
    }

    [Fact]
    public async Task ExplicitToken_OverridesTheComponentToken()
    {
        using var ctx = new BunitContext();
        var rendered = ctx.Render<LifetimeProbe>();
        var component = rendered.Instance;
        component.CaptureToken();

        using var ownCts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var completion = new TaskCompletionSource<int>();

        var execution = component.Query.Execute(new QueryParameters<int>
        {
            // Honours the token, so cancelling it settles the execution — a fetch that ignored it
            // would leave this test hanging rather than failing.
            QueryFunction = async token =>
            {
                started.SetResult();
                await using var registration = token.Register(() => completion.TrySetCanceled(token));
                return await completion.Task;
            },
            CancellationToken = ownCts.Token,
        });

        await started.Task;

        // The component's own token is untouched here, so the caller's source is what settles the
        // execution: the explicit token replaced the ambient one rather than combining with it.
        await ownCts.CancelAsync();
        await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(component.CapturedToken.IsCancellationRequested);
        Assert.False(component.Query.IsError);
    }

    [Fact]
    public async Task Disposal_DetachesCachedQuery_WithNoTokenAtTheCallSite()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddDeja();

        var rendered = ctx.Render<LifetimeProbe>();
        var component = rendered.Instance;

        var started = new TaskCompletionSource();
        var completion = new TaskCompletionSource<int>();

        var execution = component.RunKeyedQuery(QueryKey.Of("k"), _ =>
        {
            started.SetResult();
            return completion.Task;
        });

        await started.Task;
        await component.DisposeComponentAsync();

        // The component's await is released by its own token; it does not hang waiting for a
        // shared fetch it no longer cares about.
        await execution.WaitAsync(TimeSpan.FromSeconds(5));

        completion.SetResult(7);
    }

    [Fact]
    public async Task DerivedCleanupHooks_ObserveTheCancelledToken()
    {
        using var ctx = new BunitContext();
        var rendered = ctx.Render<CleanupObservesTokenProbe>();
        var component = rendered.Instance;
        component.CaptureToken();

        await component.DisposeComponentAsync();

        Assert.True(component.TokenCancelledInSyncDispose);
        Assert.True(component.TokenCancelledInAsyncDispose);
    }

    [Fact]
    public async Task Disposal_IsIdempotent_WithATokenInPlay()
    {
        using var ctx = new BunitContext();
        var rendered = ctx.Render<LifetimeProbe>();
        var component = rendered.Instance;
        component.CaptureToken();

        await component.DisposeComponentAsync();
        await component.DisposeComponentAsync();

        Assert.True(component.CapturedToken.IsCancellationRequested);
    }
}

internal static class ComponentDisposalExtensions
{
    // Blazor disposes through the interface; the base implements it explicitly.
    internal static ValueTask DisposeComponentAsync(this DejaComponentBase component)
        => ((IAsyncDisposable)component).DisposeAsync();
}
#endif
