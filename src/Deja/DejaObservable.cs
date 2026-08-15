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
/// <b>Coalescing.</b> <see cref="NotifyChanged"/> compares the current
/// <see cref="GetBindableState"/> snapshot against the last one published and returns without
/// invoking the listener when nothing an owner can bind to has moved. Several call sites converge
/// on the same transition by design — a keyed <c>Execute</c> sets <c>IsLoading</c> and the shared
/// cache entry then reports the same fetch starting — and suppressing the repeats here fixes every
/// one of them at once, including the notifications a passive subscriber receives from the entry
/// rather than from its own call.
/// </para>
/// <para>
/// <b>Thread safety.</b> <c>Attach</c> and detach run from component lifecycle methods on the
/// renderer's synchronization context, so the slot is effectively single-threaded.
/// <see cref="NotifyChanged"/> may fire from a thread-pool continuation, but only reads the field,
/// and a reference read cannot tear. The worst interleaving is a notification racing a detach and
/// invoking the listener of a component that is mid-disposal, which
/// <see cref="DejaComponentBase"/> absorbs by checking its own disposed flag before rendering.
/// Two continuations notifying concurrently may both see the snapshot as changed and render twice;
/// that is the safe direction to fail, and the renderer coalesces the pair anyway.
/// </para>
/// </remarks>
public abstract class DejaObservable : IDejaObservable
{
    private Action? _listener;
    private BindableState _lastPublished;
    private bool _hasPublished;

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

        // A fresh owner has rendered nothing yet, so the next transition must reach it even if the
        // state is byte-identical to what the previous owner last saw.
        _hasPublished = false;

        return new Attachment(this, listener);
    }

    /// <summary>
    /// A snapshot of everything an owner can bind to. <see cref="NotifyChanged"/> suppresses a
    /// notification whose snapshot equals the previous one, so overriding state must report every
    /// bindable property here — one left out becomes a property that silently stops re-rendering.
    /// </summary>
    protected abstract BindableState GetBindableState();

    /// <summary>
    /// Notifies the attached listener that bindable state changed, unless it would repeat the last
    /// notification verbatim.
    /// </summary>
    /// <remarks>
    /// Call once per state transition, after every property in that transition has been set: the
    /// listener observes whatever is set at the moment this runs, so notifying mid-transition
    /// renders a half-applied state.
    /// </remarks>
    protected void NotifyChanged()
    {
        var listener = _listener;
        if (listener is null) return;

        var current = GetBindableState();

        if (_hasPublished && current.Equals(_lastPublished)) return;

        _lastPublished = current;
        _hasPublished = true;

        listener();
    }

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
/// The bindable surface of a <see cref="DejaObservable"/> at one instant, used to suppress a
/// notification that would repeat the previous one. Compared with the struct's own value equality:
/// <see cref="Data"/> is held as <see cref="object"/> and so compares by
/// <see cref="object.Equals(object, object)"/> — reference equality for the mutable collections and
/// DTOs a query usually holds, which is the conservative direction (a re-fetched list that is
/// structurally equal but newly allocated still renders). Opt into structural comparison at the
/// cache level with <see cref="DejaOptions.StructuralComparison"/>.
/// </summary>
public readonly record struct BindableState
{
    /// <summary>Whether the state reports an execution in progress.</summary>
    public bool IsLoading { get; init; }

    /// <summary>Whether the state reports a subsequent execution in progress.</summary>
    public bool IsReFetching { get; init; }

    /// <summary>Whether the state reports a failure.</summary>
    public bool IsError { get; init; }

    /// <summary>The failure message, when one is set.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The current data, boxed so the snapshot stays non-generic.</summary>
    public object? Data { get; init; }

    /// <summary>When the current data was produced, when known.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Whether the current data came from the cache without a completed fresh fetch.</summary>
    public bool IsCachedData { get; init; }

    /// <summary>Whether the observed cache entry is invalidated or past its stale time.</summary>
    public bool IsStale { get; init; }
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
