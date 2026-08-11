namespace Deja;

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
