namespace Deja;

/// <summary>
/// Tracks a single asynchronous write and exposes its lifecycle
/// (<see cref="IsLoading"/>, <see cref="IsError"/>, <see cref="Data"/>) as bindable state,
/// notifying its attached listener as it advances so the owning component can re-render.
/// </summary>
/// <remarks>
/// Inherit <see cref="DejaComponentBase"/> in the owning component and the attachment is made (and
/// released) for you; see <see cref="IDejaObservable"/> for the single-owner rule.
/// </remarks>
/// <typeparam name="T">The type returned by the mutation.</typeparam>
public class Mutation<T> : DejaObservable, ICacheClientConsumer, IComponentLifetimeConsumer
{
    /// <summary>Creates a mutation that resolves its cache client from the owning component (if any).</summary>
    public Mutation()
    {
    }

    /// <summary>Creates a mutation bound to <paramref name="client"/>, for use outside a component.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public Mutation(DejaClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Client = client;
    }

    // Set by the constructor, by DejaComponentBase when it discovers the mutation, or per call via
    // MutationParameters<T>.Client (which takes precedence). Used only for InvalidateKeys.
    internal DejaClient? Client { get; set; }

    DejaClient? ICacheClientConsumer.Client
    {
        get => Client;
        set => Client = value;
    }

    // Handed over by DejaComponentBase when it discovers this mutation. The default,
    // CancellationToken.None, is the right fallback outside a component.
    private CancellationToken ComponentToken { get; set; }

    void IComponentLifetimeConsumer.SetComponentToken(CancellationToken token) => ComponentToken = token;

    /// <summary>True while the mutation is running.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>True when the most recent execution failed.</summary>
    public bool IsError { get; private set; }

    /// <summary>The failure message of the most recent execution, when <see cref="IsError"/> is true.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>The result of the most recent successful execution.</summary>
    public T? Data { get; private set; }

    /// <summary>
    /// Runs a mutation: the shorthand for the common case, equivalent to
    /// <c>Execute(new MutationParameters&lt;T&gt; { MutationFunction = mutationFunction })</c> with
    /// anything else — typically <see cref="MutationParameters{T}.InvalidateKeys"/> — set by
    /// <paramref name="configure"/>. <typeparamref name="T"/> comes from this mutation, so it is
    /// not restated at the call site.
    /// </summary>
    /// <param name="mutationFunction">See <see cref="MutationParameters{T}.MutationFunction"/>.</param>
    /// <param name="configure">
    /// Optional hook to set callbacks and <see cref="MutationParameters{T}.InvalidateKeys"/> before
    /// the mutation runs.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="mutationFunction"/> is <see langword="null"/>.</exception>
    public Task Execute(
        Func<Task<T>> mutationFunction,
        Action<MutationParameters<T>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mutationFunction);

        var parameters = new MutationParameters<T> { MutationFunction = mutationFunction };
        configure?.Invoke(parameters);

        return Execute(parameters);
    }

    /// <summary>
    /// Runs a cancellation-aware mutation. The token is the owning component's lifetime token, so
    /// an in-flight write is abandoned when the component is disposed:
    /// <c>_addTodo.Execute(token =&gt; Api.AddTodoAsync(title, token))</c>.
    /// </summary>
    /// <param name="mutationFunction">See <see cref="MutationParameters{T}.CancellableMutationFunction"/>.</param>
    /// <param name="configure">
    /// Optional hook to set callbacks and <see cref="MutationParameters{T}.InvalidateKeys"/> before
    /// the mutation runs.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="mutationFunction"/> is <see langword="null"/>.</exception>
    public Task Execute(
        Func<CancellationToken, Task<T>> mutationFunction,
        Action<MutationParameters<T>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mutationFunction);

        var parameters = new MutationParameters<T> { CancellableMutationFunction = mutationFunction };
        configure?.Invoke(parameters);

        return Execute(parameters);
    }

    /// <summary>
    /// Runs the mutation described by <paramref name="parameters"/>. On failure the error state is
    /// published (<see cref="IsError"/>, <see cref="ErrorMessage"/>) and the error callbacks run.
    /// An <see cref="InvalidOperationException"/> wrapping the original exception is thrown only
    /// when no error callback was supplied, so a failure nobody observes is never lost silently;
    /// wire <see cref="MutationParameters{T}.OnError"/> (or any other error callback) and the
    /// failure stays handled instead of escaping into the component lifecycle.
    /// </summary>
    public async Task Execute(MutationParameters<T> parameters)
    {
        // An explicit per-call token replaces the component's rather than combining with it.
        var token = parameters.CancellationToken ?? ComponentToken;

        // The component is already gone — a queued handler running after disposal — so starting
        // the write would issue a request nobody can observe.
        if (token.IsCancellationRequested) return;

        IsError = false;
        ErrorMessage = null;
        IsLoading = true;

        // One notification for the whole transition, once every flag above is coherent.
        NotifyChanged();

        var cancelled = false;

        try
        {
            await RunMutationAsync(parameters, token);

            // Notify before the callbacks so the listener never renders against a result the
            // mutation has accepted but not yet published.
            NotifyChanged();

            await InvokeSuccessCallbacks(parameters);

            // After OnSuccess, so a callback observing the fresh mutation result runs before the
            // queries it may depend on start refetching.
            await InvalidateKeysAsync(parameters);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The component was disposed, or the caller cancelled. Same contract as Query: not a
            // failure — no error state, no callbacks, nothing thrown at a caller that has gone.
            cancelled = true;
        }
        catch (Exception e)
        {
            Data = default;
            IsError = true;
            ErrorMessage = e.Message;
            NotifyChanged();

            var handled = await InvokeErrorCallbacks(parameters, e);
            if (!handled)
            {
                // Nothing observed this failure, so propagate rather than lose it. A callback that
                // ran means the caller surfaced it, and Execute must not escape: it is typically
                // awaited from an event handler, where a rethrow becomes an unhandled render
                // exception that tears down the component over an ordinary failed write.
                throw new InvalidOperationException("Mutation failed", e);
            }
        }
        finally
        {
            IsLoading = false;
            NotifyChanged();

            if (!cancelled)
            {
                await InvokeSettledCallbacks(parameters);
            }
        }
    }

    // Cancellation-aware functions win: a caller that supplied one wants the token honoured. Only
    // the value-returning paths write Data — a void mutation leaves any previous result standing.
    private async Task RunMutationAsync(MutationParameters<T> parameters, CancellationToken token)
    {
        if (parameters.CancellableMutationFunction is not null)
        {
            Data = await parameters.CancellableMutationFunction(token);
            return;
        }

        if (parameters.MutationFunction is not null)
        {
            Data = await parameters.MutationFunction();
            return;
        }

        if (parameters.CancellableVoidMutationFunction is not null)
        {
            await parameters.CancellableVoidMutationFunction(token);
            return;
        }

        if (parameters.VoidMutationFunction is not null)
        {
            await parameters.VoidMutationFunction();
            return;
        }

        throw new ArgumentException(
            "MutationFunction or VoidMutationFunction should be provided in order to run mutation");
    }

    // Prefix-matched: every mounted query under each key refetches in the background, in every
    // component showing that data. Without a client (no AddDeja), the keys are ignored.
    private async Task InvalidateKeysAsync(MutationParameters<T> parameters)
    {
        var client = parameters.Client ?? Client;

        if (client is null || parameters.InvalidateKeys is not { Count: > 0 } keys)
        {
            return;
        }

        foreach (var key in keys)
        {
            await client.InvalidateAsync(key);
        }
    }

    private async Task InvokeSuccessCallbacks(MutationParameters<T> parameters)
    {
        if (parameters.OnSuccessAsync is not null)
        {
            await parameters.OnSuccessAsync(Data);
        }

        parameters.OnSuccess?.Invoke(Data);
    }

    // Returns true when at least one error callback observed the exception.
    private static async Task<bool> InvokeErrorCallbacks(MutationParameters<T> parameters, Exception e)
    {
        var handled = false;

        if (e is DisplayUserException displayUserException)
        {
            if (parameters.OnDisplayUserErrorAsync is not null)
            {
                await parameters.OnDisplayUserErrorAsync(displayUserException);
                handled = true;
            }

            if (parameters.OnDisplayUserError is not null)
            {
                parameters.OnDisplayUserError(displayUserException);
                handled = true;
            }
        }

        if (parameters.OnErrorAsync is not null)
        {
            await parameters.OnErrorAsync(e);
            handled = true;
        }

        if (parameters.OnError is not null)
        {
            parameters.OnError(e);
            handled = true;
        }

        return handled;
    }

    private async Task InvokeSettledCallbacks(MutationParameters<T> parameters)
    {
        if (parameters.OnSettledAsync is not null)
        {
            await parameters.OnSettledAsync(Data);
        }

        parameters.OnSettled?.Invoke(Data);
    }
}

/// <summary>Describes one execution of a <see cref="Mutation{T}"/>: what to run and which callbacks to invoke.</summary>
/// <typeparam name="T">The type returned by the mutation.</typeparam>
public class MutationParameters<T>
{
    /// <summary>The mutation to run. Takes precedence over <see cref="VoidMutationFunction"/>.</summary>
    public Func<Task<T>>? MutationFunction { get; set; }

    /// <summary>A mutation without a return value; used when <see cref="MutationFunction"/> is not set.</summary>
    public Func<Task>? VoidMutationFunction { get; set; }

    /// <summary>
    /// The mutation to run, receiving a cancellation token. Takes precedence over every other
    /// mutation function. The token is cancelled when the owning component is disposed, so an
    /// in-flight write does not outlive the component that started it.
    /// </summary>
    public Func<CancellationToken, Task<T>>? CancellableMutationFunction { get; set; }

    /// <summary>
    /// A cancellation-aware mutation without a return value. Used when neither
    /// <see cref="CancellableMutationFunction"/> nor <see cref="MutationFunction"/> is set.
    /// </summary>
    public Func<CancellationToken, Task>? CancellableVoidMutationFunction { get; set; }

    /// <summary>
    /// Caller-owned lifetime token. Leave <see langword="null"/> (the default) inside a
    /// <see cref="DejaComponentBase"/> component and the component's own lifetime token is used,
    /// so the mutation is cancelled on dispose with nothing to wire. A value set here replaces the
    /// ambient token rather than combining with it.
    /// </summary>
    public CancellationToken? CancellationToken { get; set; }

    /// <summary>Callback invoked when the mutation succeeds.</summary>
    public Action<T?>? OnSuccess { get; set; }

    /// <summary>Async callback invoked when the mutation succeeds.</summary>
    public Func<T?, Task>? OnSuccessAsync { get; set; }

    /// <summary>Async callback invoked when the mutation fails.</summary>
    public Func<Exception, Task>? OnErrorAsync { get; set; }

    /// <summary>Callback invoked when the mutation fails.</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>Async callback invoked when the mutation fails with a <see cref="DisplayUserException"/>.</summary>
    public Func<DisplayUserException, Task>? OnDisplayUserErrorAsync { get; set; }

    /// <summary>Callback invoked when the mutation fails with a <see cref="DisplayUserException"/>.</summary>
    public Action<DisplayUserException>? OnDisplayUserError { get; set; }

    /// <summary>Callback invoked when the mutation settles (success or failure).</summary>
    public Action<T?>? OnSettled { get; set; }

    /// <summary>Async callback invoked when the mutation settles (success or failure).</summary>
    public Func<T?, Task>? OnSettledAsync { get; set; }

    /// <summary>
    /// Cache client override for this execution. Usually left <see langword="null"/>: components
    /// inheriting <see cref="DejaComponentBase"/> get the registered client handed to their
    /// mutations automatically.
    /// </summary>
    public DejaClient? Client { get; set; }

    /// <summary>
    /// Keys to invalidate (prefix-matched) after the mutation succeeds and <c>OnSuccess</c> has
    /// run. Every mounted query under them refetches in the background — in every component
    /// showing that data. Replaces manual <c>OnSuccessAsync = _ =&gt; Reload()</c> wiring.
    /// </summary>
    public IReadOnlyList<QueryKey>? InvalidateKeys { get; set; }
}
