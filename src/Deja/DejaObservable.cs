namespace Deja;

/// <summary>
/// Base implementation of <see cref="IDejaObservable"/>: holds the single listener slot and
/// notifies it. Derived state calls <see cref="NotifyChanged"/> after mutating a bindable property.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately does not implement <see cref="IDisposable"/> — detaching is the handle's job, not
/// the state's, and <see cref="Query{T}"/> has disposal semantics of its own.
/// </para>
/// <para>
/// <b>Thread safety.</b> <c>Attach</c> and detach run from component lifecycle methods on the
/// renderer's synchronization context, so the slot is effectively single-threaded.
/// <see cref="NotifyChanged"/> may fire from a thread-pool continuation, but only reads the field,
/// and a reference read cannot tear. The worst interleaving is a notification racing a detach and
/// invoking the listener of a component that is mid-disposal, which
/// <see cref="DejaComponentBase"/> absorbs by checking its own disposed flag before rendering.
/// </para>
/// </remarks>
public abstract class DejaObservable : IDejaObservable
{
    private Action? _listener;

    /// <inheritdoc />
    public IDisposable Attach(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        if (_listener is not null)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} already has a listener attached. Deja state is owned by exactly " +
                "one component; create a separate instance per component, or pass the fetched data " +
                "down as a [Parameter].");
        }

        _listener = listener;
        return new Attachment(this, listener);
    }

    /// <summary>
    /// Notifies the attached listener that bindable state changed. Safe to call when nothing is
    /// attached (no listener yet, or the owning component was disposed) — it is then a no-op.
    /// </summary>
    /// <remarks>
    /// Call once per state transition, after every property in that transition has been set: the
    /// listener observes whatever is set at the moment this runs, so notifying mid-transition
    /// renders a half-applied state.
    /// </remarks>
    protected void NotifyChanged() => _listener?.Invoke();

    private void Detach(Action listener)
    {
        // Reference check, not a null-out: guards the (pathological but cheap to defend) case of a
        // stale handle being disposed after a different listener legitimately took the slot.
        if (ReferenceEquals(_listener, listener))
        {
            _listener = null;
        }
    }

    private sealed class Attachment(DejaObservable owner, Action listener) : IDisposable
    {
        private DejaObservable? _owner = owner;

        public void Dispose()
        {
            var owner = _owner;
            _owner = null;
            owner?.Detach(listener);
        }
    }
}
