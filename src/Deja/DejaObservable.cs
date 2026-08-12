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

/// <summary>
/// State that can notify exactly one owner when it changes. Implemented by <see cref="Query{T}"/>
/// and <see cref="Mutation{T}"/>, and consumed by <see cref="DejaComponentBase"/>, which attaches
/// itself as the listener so the owning component re-renders.
/// </summary>
/// <remarks>
/// Deliberately single-listener rather than a multicast event: the one component that owns this
/// state is the only thing that may be notified by it. This is what keeps two components holding
/// their own queries from re-rendering each other.
/// </remarks>
public interface IDejaObservable
{
    /// <summary>
    /// Registers <paramref name="listener"/> as the sole owner notified when this state changes,
    /// and returns a handle that detaches it. Detaching is idempotent.
    /// </summary>
    /// <param name="listener">Invoked after each state transition.</param>
    /// <returns>A handle whose disposal detaches <paramref name="listener"/> and frees the slot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="listener"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A listener is already attached and has not been detached. State is owned by one component;
    /// to share fetched data with another component, pass <c>Data</c> down as a parameter.
    /// </exception>
    IDisposable Attach(Action listener);
}

/// <summary>
/// Non-generic surface through which <see cref="DejaComponentBase"/> hands the resolved
/// <see cref="DejaClient"/> to discovered <see cref="Query{T}"/> and <see cref="Mutation{T}"/>
/// instances without knowing their <c>T</c>. A client already set (via constructor or parameters)
/// is never overwritten.
/// </summary>
internal interface ICacheClientConsumer
{
    /// <summary>The cache client this state uses, or <see langword="null"/> for the uncached path.</summary>
    DejaClient? Client { get; set; }
}

/// <summary>
/// Non-generic surface through which <see cref="DejaComponentBase"/> hands its lifetime token to
/// discovered <see cref="Query{T}"/> and <see cref="Mutation{T}"/> instances without knowing their
/// <c>T</c>. The token is cancelled when the component is disposed, so every execution the
/// component starts is scoped to it without the component wiring anything.
/// </summary>
/// <remarks>
/// The ambient token is a fallback, not an override: an execution that sets its own
/// <c>CancellationToken</c> on the parameters uses that instead. State used outside a component
/// never receives one and behaves exactly as before.
/// </remarks>
internal interface IComponentLifetimeConsumer
{
    /// <summary>
    /// Adopts the owning component's lifetime <paramref name="token"/>, cancelled when that
    /// component is disposed.
    /// </summary>
    void SetComponentToken(CancellationToken token);
}
