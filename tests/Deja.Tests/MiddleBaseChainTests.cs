#if NET10_0_OR_GREATER
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Deja.Tests;

// Mirrors the docs demo: a middle base inheriting DejaComponentBase, leaves inheriting it.
public class MiddleBaseChainTests
{
    private abstract class MiddleBase : DejaComponentBase
    {
        private readonly Query<int> _privateBaseQuery = new();
        public Query<int> PrivateBaseQuery => _privateBaseQuery;
        public bool BaseDisposeRan { get; private set; }

        protected sealed override void OnInitialized()
        {
            base.OnInitialized();
            OnPageInitialized();
        }

        protected virtual void OnPageInitialized() { }

        protected override void Dispose()
        {
            BaseDisposeRan = true;
            base.Dispose();
        }
    }

    private sealed class Leaf : MiddleBase
    {
        public readonly Query<int> LeafQuery = new();
        public bool LeafDisposeRan { get; private set; }
        public bool PageInitRan { get; private set; }

        protected override void OnPageInitialized() => PageInitRan = true;

        protected override void Dispose()
        {
            LeafDisposeRan = true;
            base.Dispose();
        }

        protected override void BuildRenderTree(RenderTreeBuilder b) => b.AddContent(0, LeafQuery.Data);
    }

    private static bool SlotTaken(Query<int> s)
    {
        try { s.Attach(() => { }).Dispose(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    [Fact]
    public void AttachesStateOnBothMiddleBaseAndLeaf()
    {
        using var ctx = new BunitContext();
        var leaf = ctx.Render<Leaf>().Instance;

        Assert.True(leaf.PageInitRan);
        Assert.True(SlotTaken(leaf.PrivateBaseQuery));
        Assert.True(SlotTaken(leaf.LeafQuery));
    }

    [Fact]
    public async Task DisposalChainsThroughEveryLevelAndDetaches()
    {
        using var ctx = new BunitContext();
        var leaf = ctx.Render<Leaf>().Instance;

        await ctx.DisposeComponentsAsync();

        Assert.True(leaf.LeafDisposeRan);
        Assert.True(leaf.BaseDisposeRan);
        Assert.False(SlotTaken(leaf.PrivateBaseQuery));
        Assert.False(SlotTaken(leaf.LeafQuery));
    }

    [Fact]
    public void DisposalGuardStillThrows_WhenLeafRedeclaresThroughMiddleBase()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new BadLeaf());
        Assert.Contains(nameof(BadLeaf), ex.Message);
    }

    private sealed class BadLeaf : MiddleBase, IAsyncDisposable
    {
        public new ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A middle base that drops the base.OnInitialized() chain while the leaf calls through
    // correctly. The report must not pin this on the leaf, whose base call is already right.
    private abstract class SkippingMiddle : DejaComponentBase
    {
        public readonly Query<int> MiddleQuery = new();

        protected override void OnInitialized()
        {
        }
    }

    private sealed class CorrectLeaf : SkippingMiddle
    {
        public readonly Query<int> LeafQuery = new();

        protected override void OnInitialized() => base.OnInitialized();

        protected override void BuildRenderTree(RenderTreeBuilder b) => b.AddContent(0, LeafQuery.Data);
    }

    private static string CaptureConsoleError(Action action)
    {
        var original = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);

        try
        {
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        return writer.ToString();
    }

    [Fact]
    public void SkippedBaseCall_NamesEveryOverrideInTheHierarchy_NotOnlyTheLeaf()
    {
        using var ctx = new BunitContext();

        var console = CaptureConsoleError(() => ctx.Render<CorrectLeaf>());

        // The break is unobservable by the time this runs, so every candidate is named rather than
        // guessing one — naming only the leaf would point at a correct base call.
        Assert.Contains(nameof(SkippingMiddle), console);
        Assert.Contains(nameof(CorrectLeaf), console);
        Assert.Contains("base.OnInitialized()", console);
    }

    [Fact]
    public void SkippedBaseCall_LeavesTheWholeChainUnattached()
    {
        using var ctx = new BunitContext();

        CorrectLeaf leaf = null!;
        CaptureConsoleError(() => leaf = ctx.Render<CorrectLeaf>().Instance);

        // A break anywhere takes down every level, not just the type that broke it.
        Assert.False(SlotTaken(leaf.MiddleQuery));
        Assert.False(SlotTaken(leaf.LeafQuery));
    }
}
#endif
