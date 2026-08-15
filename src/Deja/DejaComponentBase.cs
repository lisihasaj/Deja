using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;

namespace Deja;

/// <summary>
/// Base component that re-renders automatically when the <see cref="Query{T}"/> and
/// <see cref="Mutation{T}"/> instances it owns change state. Declare them as fields or
/// properties; the base attaches to each one at initialization and detaches on dispose.
/// </summary>
/// <remarks>
/// <para>
/// Each component is notified only by the state it owns. Two components rendering side by side,
/// each holding its own <see cref="Query{T}"/>, never re-render because of one another —
/// Deja state notifies a single attached listener, and that listener is the owning component.
/// </para>
/// <para>
/// Renders are coalesced: while one is already queued, further notifications ride on it instead of
/// queueing their own, so several fetches completing together render the component once rather
/// than once each. Coalescing never defers a render past the notification that caused it, so
/// awaiting an <c>Execute</c> still means the component has rendered. Combined with the
/// state-level suppression in <see cref="DejaObservable"/> — which drops a notification whose
/// bindable state matches the last one published — a component renders when what it displays
/// actually changes, and not otherwise.
/// </para>
/// <para>
/// A derived component adds its own cleanup by overriding the base's hooks — <see cref="Dispose"/>
/// for synchronous cleanup, <see cref="DisposeAsync"/> for asynchronous; both run during disposal,
/// before the base detaches. Do not declare <see cref="IDisposable"/> or
/// <see cref="IAsyncDisposable"/> on a derived component — a redeclared <c>DisposeAsync</c> would
/// replace Deja's disposal entirely, and a redeclared <c>Dispose</c> would never run at all,
/// because Blazor calls only the asynchronous disposal once <see cref="IAsyncDisposable"/> is
/// present. The constructor detects either redeclaration and throws.
/// </para>
/// </remarks>
public abstract class DejaComponentBase : ComponentBase, IAsyncDisposable
{
    // Types whose hierarchy has already passed the redeclared-DisposeAsync check, so the
    // reflective walk runs once per component type rather than once per instance.
    private static readonly ConcurrentDictionary<Type, bool> _validatedDisposalContracts = new();

    private readonly List<IDisposable> _attachments = [];
    private readonly HashSet<IDejaObservable> _attached = new(ReferenceEqualityComparer.Instance);

    // Created on first use, cancelled at the top of disposal and disposed once cleanup is done.
    // Lazy so a component that never reaches for cancellation allocates nothing; the lock keeps a
    // fetch continuation reading ComponentToken from racing the renderer's disposal.
    private readonly object _lifetimeLock = new();
    private CancellationTokenSource? _lifetimeCts;
    private bool _disposed;

    // 1 while a coalesced render is queued; int rather than bool for Interlocked.
    private int _renderPending;
    private bool _baseOnInitializedRan;
    private bool _reportedBaseCallSkipped;
    private DejaClient? _resolvedClient;
    private bool _clientResolved;

    // Resolved lazily through IServiceProvider rather than `[Inject] DejaClient?` because Blazor
    // throws for an [Inject] property whose service is not registered, and the cache must stay
    // opt-in: an app that never called AddDeja() gets null here and runs the uncached path.
    // (ScanForState skips [Inject] members, so this is not mistaken for observable state.)
    [Inject] private IServiceProvider? ServiceProvider { get; set; }

    /// <summary>
    /// A token cancelled when this component is disposed. The <see cref="Query{T}"/> and
    /// <see cref="Mutation{T}"/> instances this component owns already use it, so their executions
    /// abort on navigation with nothing to wire — pass it explicitly only to work Deja does not
    /// run for you, such as a direct API call from an event handler:
    /// <c>await Api.PingAsync(ComponentToken)</c>.
    /// </summary>
    /// <remarks>
    /// There is nothing to dispose: the component owns the underlying source and releases it as
    /// part of its own disposal. Reading this after disposal returns an already-cancelled token
    /// rather than throwing, so a late continuation observes cancellation instead of an
    /// <see cref="ObjectDisposedException"/>.
    /// </remarks>
    protected CancellationToken ComponentToken
    {
        get
        {
            lock (_lifetimeLock)
            {
                // A token from the released source would throw; a pre-cancelled one says the same
                // thing safely.
                if (_disposed) return new CancellationToken(canceled: true);

                _lifetimeCts ??= new CancellationTokenSource();
                return _lifetimeCts.Token;
            }
        }
    }

    /// <summary>
    /// Rejects a derived component that redeclares <c>Dispose</c> or <c>DisposeAsync</c> instead
    /// of overriding the <see cref="Dispose"/> / <see cref="DisposeAsync"/> hooks — a redeclared
    /// <c>DisposeAsync</c> would replace Deja's disposal entirely, so the component would silently
    /// leak its attachments and never dispose the queries it owns; a redeclared <c>Dispose</c>
    /// would never be called by anything, so its cleanup would silently not run.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The derived component declares its own <c>Dispose</c> or <c>DisposeAsync</c> (typically via
    /// <c>@implements IDisposable</c> / <c>@implements IAsyncDisposable</c>) rather than
    /// overriding the base hooks.
    /// </exception>
    protected DejaComponentBase()
    {
        ThrowIfDisposalIsRedeclared();
    }

    /// <summary>
    /// Attaches the declared <see cref="Query{T}"/> / <see cref="Mutation{T}"/> state. An override
    /// in a derived component must call <c>base.OnInitialized()</c> — before its own state is
    /// used, conventionally on the first line — or nothing is attached and the component never
    /// re-renders on state changes. A skipped call is reported once as a console error on first render.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        _baseOnInitializedRan = true;
        AttachDeclaredState();
    }

    /// <inheritdoc />
    public override Task SetParametersAsync(ParameterView parameters)
    {
        // ComponentBase runs the synchronous OnInitialized inside this call on the first
        // parameter set, so the sentinel is trustworthy as soon as the base call returns —
        // the async remainder of the lifecycle does not need to be awaited first.
        var task = base.SetParametersAsync(parameters);
        ReportIfBaseOnInitializedSkipped();
        return task;
    }

    /// <summary>
    /// Runs the derived component's cleanup — its <see cref="Dispose"/> override, then its
    /// <see cref="DisposeAsync"/> override — and finally detaches from all observed state
    /// and disposes the queries this component created. Blazor calls this when the component is
    /// removed from the UI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implemented explicitly so the <c>DisposeAsync</c> name stays free for the derived override;
    /// Blazor always disposes through the interface, so nothing is lost by hiding it.
    /// </para>
    /// <para>
    /// The base deliberately does not implement <see cref="IDisposable"/>: Blazor never calls the
    /// synchronous <c>Dispose</c> once <see cref="IAsyncDisposable"/> is present, so the interface
    /// would gain nothing from the framework — while inviting a manual synchronous disposal that
    /// skips the asynchronous cleanup. Synchronous cleanup runs through the <see cref="Dispose"/>
    /// hook on this path instead.
    /// </para>
    /// </remarks>
    [SuppressMessage(
        "Major Code Smell",
        "S6966:Awaitable method should be used",
        Justification = "Dispose() here is not a synchronous stand-in for DisposeAsync() — it is " +
                        "the synchronous cleanup hook, and the asynchronous hook is awaited on " +
                        "the very next line. Both run by design.")]
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        CancellationTokenSource? lifetimeCts;

        lock (_lifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;

            // Detached before releasing the lock so ComponentToken cannot hand out a token from
            // what we are about to dispose; it returns a cancelled token from here on.
            lifetimeCts = _lifetimeCts;
            _lifetimeCts = null;
        }

        try
        {
            // Cancelled before any cleanup runs, so the derived hooks below — and any in-flight
            // execution this component started — observe cancellation while they still can. Inside
            // the try because cancellation runs every Register callback on this token: one of them
            // throwing must not skip the detaching in the finally.
            if (lifetimeCts is not null)
            {
                await lifetimeCts.CancelAsync();
            }

            // Derived cleanup first, synchronous then asynchronous, so overrides still see their
            // state attached; the base detaches only in the finally below.
            Dispose();
            await DisposeAsync();
        }
        finally
        {
            // Deja cleanup runs even when derived cleanup throws: a leaked attachment would keep
            // notifying a dead component, and a leaked query would keep its fetch in flight.
            foreach (var attachment in _attachments)
            {
                attachment.Dispose();
            }

            _attachments.Clear();

            // Disposing an owned Query cancels its in-flight fetch, so navigating away aborts the
            // request instead of letting it run to completion against a component that is gone.
            foreach (var state in _attached)
            {
                if (state is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _attached.Clear();

            // Last: every registration against this token is released by now, so disposal cannot
            // race a callback still running.
            lifetimeCts?.Dispose();

            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Override to add synchronous cleanup; runs during disposal, before the
    /// <see cref="DisposeAsync"/> override and before the base detaches from observed state. Call
    /// <c>base.Dispose()</c> from an override. Do not declare <c>@implements IDisposable</c> —
    /// this hook is the synchronous half of disposal, and Blazor would never call a
    /// <c>Dispose</c> of the component's own once <see cref="IAsyncDisposable"/> is in the
    /// hierarchy.
    /// </summary>
    [SuppressMessage(
        "Major Code Smell",
        "S2953:Methods named \"Dispose\" should implement \"IDisposable.Dispose\"",
        Justification = "Deliberate: this is the synchronous cleanup hook, invoked from the " +
                        "base's IAsyncDisposable disposal. Implementing IDisposable here would " +
                        "invite a synchronous disposal path that skips the DisposeAsync override.")]
    protected virtual void Dispose()
    {
    }

    /// <summary>
    /// Override to add asynchronous cleanup; runs during disposal, after the
    /// <see cref="Dispose"/> override and before the base detaches from observed state. Call
    /// <c>base.DisposeAsync()</c> from an override. Do not declare
    /// <c>@implements IAsyncDisposable</c> — the base already implements it, and this override is
    /// its extension point.
    /// </summary>
    protected virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Every redeclaration this rejects declares a disposal method whose base definition is not
    // ours; the one supported shape, `protected override`, roots at our virtual hooks and passes.
    // Throws rather than reporting (as the OnInitialized guard does) because there is no
    // degraded-but-working state to preserve: a redeclared DisposeAsync replaces the disposal that
    // detaches state, and a redeclared Dispose is dead code its author believes is cleanup.
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072:Target parameter does not satisfy DynamicallyAccessedMembersAttribute",
        Justification = "GetType() is annotated with All by the runtime for the instance's own type; " +
                        "the base-type walk is covered by ThrowIfTypeRedeclaresDisposal's own annotation.")]
    private void ThrowIfDisposalIsRedeclared()
    {
        var componentType = GetType();
        if (_validatedDisposalContracts.ContainsKey(componentType)) return;

        for (var type = componentType; type is not null && type != typeof(DejaComponentBase); type = type.BaseType)
        {
            ThrowIfTypeRedeclaresDisposal(type);
        }

        _validatedDisposalContracts.TryAdd(componentType, true);
    }

    // Split out so the annotation applies to the walked type, including base types.
    private static void ThrowIfTypeRedeclaresDisposal(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)]
        Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var method in type.GetMethods(flags))
        {
            // An explicit re-implementation compiles under the interface-qualified name, so a
            // plain name test alone would miss it.
            var redeclaresDispose = method.Name is "Dispose" or "System.IDisposable.Dispose";
            var redeclaresDisposeAsync =
                method.Name is "DisposeAsync" or "System.IAsyncDisposable.DisposeAsync";

            if (!redeclaresDispose && !redeclaresDisposeAsync)
            {
                continue;
            }

            // Only the parameterless surface is the disposal contract; a Dispose(bool) or
            // DisposeAsync(CancellationToken) helper is an implementation detail.
            if (method.GetParameters().Length != 0)
            {
                continue;
            }

            // A `protected override` roots at the base's virtual hook — that is the supported path.
            if (method.GetBaseDefinition().DeclaringType == typeof(DejaComponentBase))
            {
                continue;
            }

            var declaringType = method.DeclaringType?.Name ?? type.Name;

            throw redeclaresDisposeAsync
                ? new InvalidOperationException(
                    $"[Deja] {declaringType} declares its own DisposeAsync, " +
                    "which replaces Deja's disposal entirely — the component would never detach " +
                    "from its Query/Mutation state or dispose the queries it owns. Remove " +
                    "'@implements IAsyncDisposable' (the base already implements it) and declare " +
                    "the component's asynchronous cleanup as " +
                    "'protected override async ValueTask DisposeAsync()' instead, calling " +
                    "base.DisposeAsync() before returning.")
                : new InvalidOperationException(
                    $"[Deja] {declaringType} declares its own Dispose, which nothing would ever " +
                    "call — Blazor disposes only through IAsyncDisposable once it is present, " +
                    "and Deja routes synchronous cleanup through its Dispose hook. Remove " +
                    "'@implements IDisposable' and declare the component's synchronous cleanup " +
                    "as 'protected override void Dispose()' instead, calling base.Dispose() " +
                    "before returning.");
        }
    }

    // Skipping base.OnInitialized() compiles and renders once, then the component silently stops
    // reacting. Written to stderr, which the WebAssembly runtime surfaces as console.error.
    private void ReportIfBaseOnInitializedSkipped()
    {
        if (_baseOnInitializedRan || _reportedBaseCallSkipped) return;

        _reportedBaseCallSkipped = true;
        Console.Error.WriteLine(
            $"[Deja] {GetType().Name} overrides OnInitialized without calling base.OnInitialized(). " +
            "Declared Query/Mutation state was never attached, so this component will not re-render " +
            "when that state changes. Call base.OnInitialized() first, or attach state explicitly " +
            "with Observe().");
    }

    /// <summary>
    /// Re-renders this component whenever <paramref name="state"/> changes, and detaches when the
    /// component is disposed. Called automatically for state held in fields or properties at
    /// initialization; call it for state created later. Attaching the same instance twice is a no-op.
    /// </summary>
    /// <typeparam name="TState">The concrete state type, preserved so the result can be used inline.</typeparam>
    /// <param name="state">The query or mutation this component owns.</param>
    /// <returns><paramref name="state"/>, so it can be used inline: <c>_detail = Observe(new Query&lt;Detail&gt;());</c></returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The component has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Another component already owns <paramref name="state"/>. Give this component its own
    /// instance, or pass the fetched data down as a <see cref="ParameterAttribute"/>.
    /// </exception>
    protected TState Observe<TState>(TState state) where TState : IDejaObservable
    {
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_attached.Add(state)) return state;

        _attachments.Add(state.Attach(OnStateChanged));

        // Hand the registered cache client (if any) to discovered queries and mutations that
        // don't already have one, so keyed queries share the app-wide cache with zero wiring.
        if (state is ICacheClientConsumer { Client: null } consumer)
        {
            consumer.Client = ResolveClient();
        }

        // Scopes every execution to the component's lifetime, so a derived component neither
        // declares a CancellationTokenSource nor threads a token through its call sites. An
        // execution that sets its own token on the parameters still wins.
        if (state is IComponentLifetimeConsumer lifetimeConsumer)
        {
            lifetimeConsumer.SetComponentToken(ComponentToken);
        }

        return state;
    }

    private DejaClient? ResolveClient()
    {
        if (!_clientResolved)
        {
            _clientResolved = true;
            _resolvedClient = ServiceProvider?.GetService(typeof(DejaClient)) as DejaClient;
        }

        return _resolvedClient;
    }

    // Notifications arrive on whatever thread the fetch continuation landed on, so the render is
    // marshalled onto the renderer's synchronization context. Fire-and-forget is correct: the only
    // way InvokeAsync faults is a renderer that is already gone, which is not actionable.
    //
    // Coalescing: while a render is already queued, later notifications ride on it rather than
    // queueing their own. This is deliberately not a deferred/batched render — the render still
    // happens inline when the notification arrives on the renderer's context, so awaiting an
    // Execute still means the component has rendered. What it collapses is the burst case, where
    // several fetch continuations complete together off the renderer's context.
    private void OnStateChanged()
    {
        if (_disposed) return;

        // Interlocked, not a plain write: notifications arrive from arbitrary fetch continuations,
        // and two racing to schedule would otherwise both see "none pending" and queue two renders.
        if (Interlocked.Exchange(ref _renderPending, 1) == 1) return;

        _ = InvokeAsync(RenderCoalesced);
    }

    private void RenderCoalesced()
    {
        // Released before rendering, not after: a notification raised while StateHasChanged runs
        // describes state this render may already have passed, so it must be able to schedule the
        // next one. Clearing afterwards would drop it and leave the component stale.
        Volatile.Write(ref _renderPending, 0);

        if (_disposed) return;

        StateHasChanged();
    }

    // Fields and properties are scanned once at init. State assigned later — a Query created
    // inside an event handler — must be passed to Observe() explicitly.
    //
    // Trimming: nothing in the compiled output reads the derived component's fields by name, so a
    // trimmer would otherwise be free to remove them. The annotation roots them for every type
    // reaching this method; the types themselves are already rooted by the Blazor renderer.
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072:Target parameter does not satisfy DynamicallyAccessedMembersAttribute",
        Justification = "GetType() is annotated with All by the runtime for the instance's own type; " +
                        "the base-type walk is covered by ScanForState's own annotation.")]
    private void AttachDeclaredState()
    {
        for (var type = GetType(); type is not null && type != typeof(DejaComponentBase); type = type.BaseType)
        {
            ScanForState(type);
        }
    }

    // Split out so the annotation applies to the walked type, including base types.
    private void ScanForState(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields |
            DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties)]
        Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(flags))
        {
            // Auto-properties compile to a backing field, so scanning fields blindly would attach
            // [Parameter] / [CascadingParameter] / [Inject] state through the back door. The
            // property loop below handles these members, where the attributes are visible.
            if (field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                continue;
            }

            if (field.GetValue(this) is IDejaObservable state)
            {
                Observe(state);
            }
        }

        foreach (var property in type.GetProperties(flags))
        {
            // Parameters are owned by the parent that passed them — attaching would both steal
            // the parent's listener slot (throwing) and dispose state we don't own.
            if (property.GetIndexParameters().Length != 0 ||
                !property.CanRead ||
                property.IsDefined(typeof(ParameterAttribute), inherit: true) ||
                property.IsDefined(typeof(CascadingParameterAttribute), inherit: true) ||
                property.IsDefined(typeof(InjectAttribute), inherit: true))
            {
                continue;
            }

            if (property.GetValue(this) is IDejaObservable state)
            {
                Observe(state);
            }
        }
    }
}
