#if NET10_0_OR_GREATER
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Deja.Tests;

public class DejaComponentBaseTests
{
    // A component declaring state every way the discovery scan has to handle.
    private sealed class Probe : DejaComponentBase
    {
        public readonly Query<int> FieldQuery = new();
        public readonly Mutation<int> FieldMutation = new();
        public Query<int> PropertyQuery { get; } = new();

        [Parameter] public Query<int>? ParameterQuery { get; set; }
        [CascadingParameter] public Query<int>? CascadingQuery { get; set; }
        [Inject] public Query<int> InjectedQuery { get; set; } = default!;

        public int RenderCount { get; private set; }

        public Query<int> ObserveLate(Query<int> query) => Observe(query);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            RenderCount++;
            builder.AddContent(0, FieldQuery.Data);
        }
    }

    // Declares state on a type derived from another DejaComponentBase: the scan must walk bases.
    private abstract class ProbeBase : DejaComponentBase
    {
        public readonly Query<int> BaseQuery = new();
    }

    private sealed class DerivedProbe : ProbeBase
    {
        public readonly Query<int> DerivedQuery = new();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
            => builder.AddContent(0, DerivedQuery.Data);
    }

    // The one consumer mistake the base cannot prevent, only detect: an OnInitialized override
    // that never calls base.OnInitialized(), so declared state is never attached.
    private sealed class SkipsBaseCallProbe : DejaComponentBase
    {
        public readonly Query<int> FieldQuery = new();

        protected override void OnInitialized()
        {
        }
    }

    private sealed class CallsBaseCallProbe : DejaComponentBase
    {
        protected override void OnInitialized() => base.OnInitialized();
    }

    // Overrides the synchronous hook — the supported home for sync cleanup, replacing the
    // Blazor-docs pattern (@implements IDisposable) that the constructor now rejects.
    private sealed class SyncDisposeProbe : DejaComponentBase
    {
        public readonly Query<int> FieldQuery = new();

        public bool DisposeRan { get; private set; }
        public bool StateWasStillAttached { get; private set; }

        protected override void Dispose()
        {
            DisposeRan = true;

            // Most-derived cleanup runs first, so the attachment must still hold the slot here.
            try
            {
                FieldQuery.Attach(() => { });
            }
            catch (InvalidOperationException)
            {
                StateWasStillAttached = true;
            }

            base.Dispose();
        }
    }

    private sealed class AsyncOverrideProbe : DejaComponentBase
    {
        public readonly Query<int> FieldQuery = new();

        public bool AsyncOverrideRan { get; private set; }

        protected override async ValueTask DisposeAsync()
        {
            await Task.Yield();
            AsyncOverrideRan = true;
            await base.DisposeAsync();
        }
    }

    private sealed class BothCleanupsProbe : DejaComponentBase
    {
        public List<string> CleanupOrder { get; } = [];

        protected override void Dispose()
        {
            CleanupOrder.Add("sync");
            base.Dispose();
        }

        protected override ValueTask DisposeAsync()
        {
            CleanupOrder.Add("async");
            return base.DisposeAsync();
        }
    }

    // The forbidden move: what `@implements IAsyncDisposable` plus a public DisposeAsync compiles
    // to. The re-implementation would replace the base's disposal, so construction must throw.
    private sealed class ReimplementsAsyncDisposableProbe : DejaComponentBase, IAsyncDisposable
    {
        public new ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Half the move: a public DisposeAsync without re-listing the interface. Blazor still reaches
    // the base's disposal, but this method is dead code its author believes is cleanup.
    private sealed class HidesDisposeAsyncProbe : DejaComponentBase
    {
        public bool CleanupRan { get; private set; }

        public new ValueTask DisposeAsync()
        {
            CleanupRan = true;
            return ValueTask.CompletedTask;
        }
    }

    // The redeclaration may sit on a base type the concrete component inherits through.
    private abstract class ReimplementingBase : DejaComponentBase, IAsyncDisposable
    {
        public new ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InheritsReimplementationProbe : ReimplementingBase;

    private sealed class ThrowingDisposeProbe : DejaComponentBase
    {
        public readonly Query<int> FieldQuery = new();

        protected override void Dispose() =>
            throw new InvalidOperationException("derived cleanup failed");
    }

    // The forbidden sync move: what `@implements IDisposable` plus a public Dispose compiles to.
    // Nothing would ever call it — Blazor disposes only through IAsyncDisposable — so
    // construction must throw.
    private sealed class ReimplementsDisposableProbe : DejaComponentBase, IDisposable
    {
        public new void Dispose()
        {
        }
    }

    // Half the move: a public Dispose without re-listing the interface — dead code its author
    // believes is cleanup.
    private sealed class HidesDisposeProbe : DejaComponentBase
    {
        public bool CleanupRan { get; private set; }

        public new void Dispose() => CleanupRan = true;
    }

    // Parameterful helpers named after the disposal methods are implementation details, not
    // redeclarations of the disposal contract — the guard must let them through.
    private sealed class DisposalHelpersProbe : DejaComponentBase
    {
        public int HelperCalls { get; private set; }

        public void Dispose(bool disposing)
        {
            if (disposing) HelperCalls++;
        }

        public ValueTask DisposeAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            HelperCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        // Satisfies Probe's [Inject] property with state the component must NOT take ownership of.
        ctx.Services.AddSingleton(new Query<int>());
        return ctx;
    }

    private static Task Fetch(Query<int> query, int value) =>
        query.Execute(new QueryParameters<int> { QueryFunction = _ => Task.FromResult(value) });

    [Fact]
    public void AttachesFieldsAndNonParameterProperties_AtInit()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<Probe>().Instance;

        // The slot is taken, so an outside Attach must be refused.
        Assert.Throws<InvalidOperationException>(() => probe.FieldQuery.Attach(() => { }));
        Assert.Throws<InvalidOperationException>(() => probe.FieldMutation.Attach(() => { }));
        Assert.Throws<InvalidOperationException>(() => probe.PropertyQuery.Attach(() => { }));
    }

    [Fact]
    public void DoesNotAttach_ParameterCascadingOrInjectedState()
    {
        using var ctx = CreateContext();
        var parameterQuery = new Query<int>();
        var cascadingQuery = new Query<int>();
        var injected = ctx.Services.GetRequiredService<Query<int>>();

        ctx.Render<Probe>(ps => ps
            .Add(p => p.ParameterQuery, parameterQuery)
            .AddCascadingValue(cascadingQuery));

        // The parent still owns these, so their slots must be free for it to claim. Auto-properties
        // compile to a backing field, so this also guards the field scan from attaching them behind
        // the property loop's attribute checks.
        parameterQuery.Attach(() => { }).Dispose();
        cascadingQuery.Attach(() => { }).Dispose();
        injected.Attach(() => { }).Dispose();
    }

    [Fact]
    public void SharedInjectedState_IsNeverClaimed_EvenByManyComponents()
    {
        using var ctx = CreateContext();
        var injected = ctx.Services.GetRequiredService<Query<int>>();

        // A singleton Query resolved into two components: if either claimed the [Inject] property,
        // the second render would throw on the single-listener rule.
        ctx.Render<Probe>();
        ctx.Render<Probe>();

        injected.Attach(() => { }).Dispose();
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeStateItDoesNotOwn()
    {
        using var ctx = CreateContext();
        var parameterQuery = new Query<int>();
        var injected = ctx.Services.GetRequiredService<Query<int>>();

        ctx.Render<Probe>(ps => ps.Add(p => p.ParameterQuery, parameterQuery));
        await ctx.DisposeComponentsAsync();

        // A disposed query ignores Execute; these must still work for their real owners.
        await Fetch(parameterQuery, 3);
        await Fetch(injected, 4);

        Assert.Equal(3, parameterQuery.Data);
        Assert.Equal(4, injected.Data);
    }

    [Fact]
    public async Task StateChange_RendersTheOwningComponent()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<Probe>();
        var probe = cut.Instance;
        var before = probe.RenderCount;

        await cut.InvokeAsync(() => Fetch(probe.FieldQuery, 42));

        Assert.True(probe.RenderCount > before);
        cut.MarkupMatches("42");
    }

    [Fact]
    public async Task Siblings_AreIsolated_StateInOneRendersOnlyThatOne()
    {
        using var ctx = CreateContext();
        var leftCut = ctx.Render<Probe>();
        var right = ctx.Render<Probe>().Instance;

        var rightRendersBefore = right.RenderCount;

        await leftCut.InvokeAsync(() => Fetch(leftCut.Instance.FieldQuery, 1));

        Assert.Equal(1, leftCut.Instance.FieldQuery.Data);
        Assert.Equal(rightRendersBefore, right.RenderCount);
        Assert.Equal(0, right.FieldQuery.Data);
    }

    [Fact]
    public void AttachesStateDeclaredOnBaseTypes()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<DerivedProbe>().Instance;

        Assert.Throws<InvalidOperationException>(() => probe.DerivedQuery.Attach(() => { }));
        Assert.Throws<InvalidOperationException>(() => probe.BaseQuery.Attach(() => { }));
    }

    [Fact]
    public async Task Dispose_DetachesAndDisposesOwnedQueries_CancellingInFlightFetch()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<Probe>().Instance;

        var started = new TaskCompletionSource();
        var observedCancellation = new TaskCompletionSource();

        var execution = probe.FieldQuery.Execute(new QueryParameters<int>
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

                return 1;
            },
        });

        await started.Task;
        await ctx.DisposeComponentsAsync();
        await execution;

        // Disposal cancelled the in-flight fetch...
        await observedCancellation.Task;

        // ...and freed the listener slot, proving the state no longer holds the component.
        probe.FieldQuery.Attach(() => { }).Dispose();
    }

    [Fact]
    public async Task Observe_AfterInit_AttachesAndReturnsItsArgument()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<Probe>();
        var probe = cut.Instance;
        var late = new Query<int>();

        Assert.Same(late, probe.ObserveLate(late));

        var before = probe.RenderCount;
        await cut.InvokeAsync(() => Fetch(late, 5));

        Assert.True(probe.RenderCount > before);
    }

    [Fact]
    public async Task Dispose_DisposesLateObservedState()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<Probe>().Instance;
        var late = probe.ObserveLate(new Query<int>());

        await ctx.DisposeComponentsAsync();

        // Disposed queries ignore Execute, which is how ownership transfer is observable here.
        await Fetch(late, 9);
        Assert.Equal(0, late.Data);
    }

    [Fact]
    public void Observe_SameInstanceTwice_IsNoOp()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<Probe>().Instance;

        // Already attached by discovery; re-observing must not throw on the single-listener rule.
        Assert.Same(probe.FieldQuery, probe.ObserveLate(probe.FieldQuery));
    }

    [Fact]
    public async Task Observe_AfterDispose_Throws()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<Probe>().Instance;

        await ctx.DisposeComponentsAsync();

        Assert.Throws<ObjectDisposedException>(() => probe.ObserveLate(new Query<int>()));
    }

    // Console.SetError swaps a process-global writer, so a parallel test could interleave writes.
    // The assertions only check for this suite's own marker, which no other test emits.
    private static string CaptureConsoleError(Action act)
    {
        var original = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);

        try
        {
            act();
        }
        finally
        {
            Console.SetError(original);
        }

        return writer.ToString();
    }

    [Fact]
    public void ReportsConsoleError_WhenOverrideSkipsBaseOnInitialized()
    {
        using var ctx = CreateContext();
        SkipsBaseCallProbe probe = null!;

        var console = CaptureConsoleError(() => probe = ctx.Render<SkipsBaseCallProbe>().Instance);

        Assert.Contains($"[Deja] {nameof(SkipsBaseCallProbe)}", console);
        Assert.Contains("base.OnInitialized()", console);

        // The error reports a real failure: the scan never ran, so the slot is still free.
        probe.FieldQuery.Attach(() => { }).Dispose();
    }

    [Fact]
    public void ReportsConsoleError_OncePerComponent_NotPerParameterSet()
    {
        using var ctx = CreateContext();

        var console = CaptureConsoleError(() =>
        {
            var cut = ctx.Render<SkipsBaseCallProbe>();
            cut.Render();
            cut.Render();
        });

        Assert.Equal(1, console.Split("[Deja]").Length - 1);
    }

    [Fact]
    public void DoesNotReportConsoleError_WithoutAnOverride_OrWithTheBaseCall()
    {
        using var ctx = CreateContext();

        var console = CaptureConsoleError(() =>
        {
            ctx.Render<Probe>();
            ctx.Render<CallsBaseCallProbe>();
        });

        Assert.DoesNotContain("[Deja]", console);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<Probe>().Instance;

        await ctx.DisposeComponentsAsync();
        await ((IAsyncDisposable)probe).DisposeAsync();
    }

    [Fact]
    public async Task DisposeOverride_RunsBeforeTheBaseDetaches()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<SyncDisposeProbe>().Instance;

        await ctx.DisposeComponentsAsync();

        Assert.True(probe.DisposeRan);
        Assert.True(probe.StateWasStillAttached);

        // Base cleanup still ran afterwards: the slot is free again.
        probe.FieldQuery.Attach(() => { }).Dispose();
    }

    [Fact]
    public async Task DisposeAsyncOverride_RunsOnDisposal()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<AsyncOverrideProbe>().Instance;

        await ctx.DisposeComponentsAsync();

        Assert.True(probe.AsyncOverrideRan);
        probe.FieldQuery.Attach(() => { }).Dispose();
    }

    [Fact]
    public async Task DisposeOverride_RunsBefore_DisposeAsyncOverride()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<BothCleanupsProbe>().Instance;

        await ctx.DisposeComponentsAsync();

        Assert.Equal(["sync", "async"], probe.CleanupOrder);
    }

    [Fact]
    public async Task DisposeOverrideThrows_BaseCleanupStillRuns()
    {
        using var ctx = CreateContext();
        var probe = ctx.Render<ThrowingDisposeProbe>().Instance;

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IAsyncDisposable)probe).DisposeAsync());

        // The exception escaped, but the owned query was still detached and disposed.
        probe.FieldQuery.Attach(() => { }).Dispose();
    }

    [Fact]
    public void Constructor_Throws_WhenDerivedReimplementsIAsyncDisposable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ReimplementsAsyncDisposableProbe());

        Assert.Contains(nameof(ReimplementsAsyncDisposableProbe), ex.Message);
        // The error must hand the author the supported pattern, not just the prohibition.
        Assert.Contains("protected override async ValueTask DisposeAsync()", ex.Message);
    }

    [Fact]
    public void Constructor_Throws_WhenDerivedHidesDisposeAsync_WithoutTheInterface()
    {
        Assert.Throws<InvalidOperationException>(() => new HidesDisposeAsyncProbe());
    }

    [Fact]
    public void Constructor_Throws_WhenTheRedeclarationLivesOnABaseType()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new InheritsReimplementationProbe());

        // The message names the type that declares the offending method, not the concrete leaf.
        Assert.Contains(nameof(ReimplementingBase), ex.Message);
    }

    [Fact]
    public void Constructor_Throws_WhenDerivedReimplementsIDisposable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ReimplementsDisposableProbe());

        Assert.Contains(nameof(ReimplementsDisposableProbe), ex.Message);
        // The error must hand the author the supported pattern, not just the prohibition.
        Assert.Contains("protected override void Dispose()", ex.Message);
    }

    [Fact]
    public void Constructor_Throws_WhenDerivedHidesDispose_WithoutTheInterface()
    {
        Assert.Throws<InvalidOperationException>(() => new HidesDisposeProbe());
    }

    [Fact]
    public void Constructor_DoesNotThrow_ForOverridesOfTheDisposalHooks()
    {
        // The supported patterns must construct cleanly, including repeat constructions that hit
        // the per-type validation cache.
        _ = new AsyncOverrideProbe();
        _ = new AsyncOverrideProbe();
        _ = new SyncDisposeProbe();
        _ = new BothCleanupsProbe();
    }

    [Fact]
    public void Constructor_DoesNotThrow_ForParameterfulDisposalHelpers()
    {
        _ = new DisposalHelpersProbe();
    }
}
#endif
