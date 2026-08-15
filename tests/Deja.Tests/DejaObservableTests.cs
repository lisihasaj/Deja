namespace Deja.Tests;

public class DejaObservableTests
{
    private sealed class TestObservable : DejaObservable
    {
        private int _version;

        // Each Raise reports different state, so these tests exercise the attach/detach mechanics
        // rather than the equal-state suppression (covered by NotifyChanged_* below).
        public void Raise()
        {
            _version++;
            NotifyChanged();
        }

        public void RaiseUnchanged() => NotifyChanged();

        protected override BindableState GetBindableState() => new() { Data = _version };
    }

    [Fact]
    public void NotifyChanged_WithUnchangedState_DoesNotNotify()
    {
        var observable = new TestObservable();
        var notifications = 0;
        using var attachment = observable.Attach(() => notifications++);

        observable.Raise();
        observable.RaiseUnchanged();
        observable.RaiseUnchanged();

        Assert.Equal(1, notifications);
    }

    [Fact]
    public void NotifyChanged_AfterReattach_NotifiesEvenWhenStateIsUnchanged()
    {
        var observable = new TestObservable();
        observable.Attach(() => { }).Dispose();

        // A new owner has rendered nothing yet, so suppression must not carry over from the old one.
        var notifications = 0;
        using var attachment = observable.Attach(() => notifications++);
        observable.RaiseUnchanged();

        Assert.Equal(1, notifications);
    }

    [Fact]
    public void Attach_NullListener_Throws()
    {
        var observable = new TestObservable();

        Assert.Throws<ArgumentNullException>(() => observable.Attach(null!));
    }

    [Fact]
    public void NotifyChanged_WithNoListener_IsNoOp()
    {
        var observable = new TestObservable();

        observable.Raise();
    }

    [Fact]
    public void Attach_ReturnsHandle_ThatStopsNotifications()
    {
        var observable = new TestObservable();
        var notifications = 0;

        var attachment = observable.Attach(() => notifications++);
        observable.Raise();
        Assert.Equal(1, notifications);

        attachment.Dispose();
        observable.Raise();
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void Attach_SecondListener_ThrowsWithActionableMessage()
    {
        var observable = new TestObservable();
        using var attachment = observable.Attach(() => { });

        var ex = Assert.Throws<InvalidOperationException>(() => observable.Attach(() => { }));

        Assert.Contains(nameof(TestObservable), ex.Message, StringComparison.Ordinal);
        Assert.Contains("[Parameter]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleHandle_Disposed_DoesNotDetachTheCurrentListener()
    {
        var observable = new TestObservable();
        var current = 0;

        var stale = observable.Attach(() => { });
        stale.Dispose();

        using var live = observable.Attach(() => current++);

        // Disposing the already-detached handle must not evict the listener that took the slot.
        stale.Dispose();
        observable.Raise();

        Assert.Equal(1, current);
    }

    [Fact]
    public void Detach_ThenReattach_SameListenerInstance_Works()
    {
        var observable = new TestObservable();
        var notifications = 0;
        Action listener = () => notifications++;

        var first = observable.Attach(listener);
        first.Dispose();

        using var second = observable.Attach(listener);
        observable.Raise();

        Assert.Equal(1, notifications);
    }
}
