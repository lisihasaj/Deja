# Deja

**Data fetching, mutation and caching primitives for Blazor — WebAssembly and Server.**

📖 **[Documentation & live demos](https://lisihasaj.github.io/Deja/)**

Déjà vu: you have seen this data before, and Deja remembers it for you.

> **Status: early preview.** Deja is being extracted from a production Blazor application. The
> `Query<T>` / `Mutation<T>` API mirrors that battle-tested code, and the shared cache
> (`DejaClient`: central keyed cache, invalidation, stale times) is now in. Expect
> breaking changes while the version is 0.x.

## What's here today

### `DejaComponentBase` — automatic re-rendering

- Inherit it and every `Query<T>` / `Mutation<T>` the component declares re-renders it
  automatically: no `INotifyPropertyChanged` subscription, no handler, no `IDisposable`.
- **Strict per-component isolation.** A `Query<T>` notifies exactly one component — the one that
  owns it. Components sharing a `QueryKey` are updated independently by the cache entry they each
  observe — never by one another; without a shared key, two components holding their own
  `Query<T>` can never re-render each other at all.
- **Two renders per request — first fetch or hundredth.** One when the request starts, one when it
  finishes. A transition that changes nothing bindable renders nothing at all, and concurrent
  requests share a queued render instead of each forcing their own, so three queries loading
  together cost six renders rather than nine. Coalescing never defers a render past the change that
  caused it: after `await Execute(...)` the component has rendered.
- Disposing the component detaches from everything it observed and cancels its in-flight fetches
  and writes — no `CancellationTokenSource` to declare, no token to thread through call sites.

### `Query<T>` — declarative async reads

- Bindable lifecycle state: `IsLoading`, `IsReFetching`, `IsError`,
  `ErrorMessage`, `Data`, `ReFetchCount` — one notification per state transition, so components
  re-render once per change rather than once per property.
- **Supersede & cancel**: a newer `Execute` cancels the in-flight one (all the way down to the
  server), and a slow stale response can never overwrite fresh data.
- **In-flight deduplication**: concurrent calls sharing a `QueryKey` join the same execution
  instead of hitting the server twice.
- **Cancellation-aware fetches**: Deja hands your `QueryFunction` a token that is cancelled on
  supersede or dispose; ignore it with `_ =>` when the fetch can't observe cancellation. Inside a
  `DejaComponentBase` the component's own lifetime token is used automatically — see
  [Cancellation](#cancellation).
- **Timeout resilience**: an `HttpClient` timeout caused by a frozen background browser tab is
  retried once automatically (queries are idempotent reads).
- Success / error / settled callbacks, sync and async, plus dedicated callbacks for
  `DisplayUserException` — errors whose message is meant for the end user. Awaited, and run on
  every completed `Execute` including a cache hit that never fetched, so a request that depends on
  a previous result chains from `OnSuccessAsync` — see
  [Chaining dependent requests](#chaining-dependent-requests).
- **Manual refetch**: `Refetch()` re-runs the last `Execute` with a forced fresh fetch — see
  [Manual refetch](#manual-refetch).

### `Mutation<T>` — declarative async writes

- Bindable `IsLoading`, `IsError`, `ErrorMessage`, `Data` state.
- Typed (`MutationFunction`) or void (`VoidMutationFunction`) mutations, each with a
  cancellation-aware counterpart (`CancellableMutationFunction`, `CancellableVoidMutationFunction`)
  so an in-flight write does not outlive the component that started it.
- Success / error / settled callbacks, sync and async, with `DisplayUserException` support.
  Awaited, so a mutation can drive follow-up reads or writes from `OnSuccessAsync` — see
  [Chaining dependent requests](#chaining-dependent-requests).
- `InvalidateKeys`: after a successful mutation, the listed keys are invalidated (prefix-matched)
  and every mounted query under them refetches — in every component showing that data.

### `DejaClient` — the shared query cache (opt-in)

Register it once and keyed queries share an app-wide cache:

```csharp
// Program.cs
builder.Services.AddDeja();

// or configured:
builder.Services.AddDeja(options =>
{
    options.DefaultStaleTime = TimeSpan.FromSeconds(30);
    options.DefaultCacheTime = TimeSpan.FromMinutes(10);
});
```

- **Instant renders from cache.** A query whose `QueryKey` is already cached publishes the data
  immediately, then revalidates in the background if it is stale. **The default stale time is
  zero**: cached data always renders instantly *and* is refetched in the background on the next
  mount — "cached" does not mean "no request" until you raise `DefaultStaleTime` (globally), or
  `StaleTime` (per query), or `SetDefaults` (per key prefix).
- **One fetch per key, app-wide.** Two components asking for the same key share one cache entry
  and one in-flight request, and both re-render when the entry changes.
- **Structured, hierarchical keys.** `QueryKey.Of("todos", "detail", 5)` — segment order matters,
  dictionary segments are order-normalized, and string literals still work (`QueryKey = "todos"`).
  Prefix invalidation makes hierarchies pay off: `client.InvalidateAsync(QueryKey.Of("todos"))`
  invalidates `["todos", "done"]` and `["todos", 5]` in one call.
- **Manual cache access.** `GetData` / `TryGetData` / `SetData` (including an updater form),
  `Remove`, `Clear`, `RefetchAsync`, and `GetState` for diagnostics.
- **Bounded memory.** Entries with no subscribers are evicted after `CacheTime` (default 5
  minutes); `MaxEntries` adds an optional LRU cap. Subscribed entries are never evicted.
- **Genuinely opt-in.** No `AddDeja()`, no cache: every query behaves exactly as before. Queries
  without a `QueryKey` never touch the cache even with a client registered.

> **Blazor Server note:** `AddDeja` registers the client **Scoped** — one cache per user circuit.
> Do not re-register it as a singleton: that would share one user's cached API responses with
> every other connected user.

## Quick start

```razor
@inherits DejaComponentBase
@inject TodoApi Api

@if (_todos.IsLoading)
{
    <p>Loading…</p>
}
else if (_todos.IsError)
{
    <p role="alert">@_todos.ErrorMessage</p>
}
else
{
    <ul>
        @foreach (var todo in _todos.Data ?? [])
        {
            <li>@todo.Title</li>
        }
    </ul>
}

@code {
    private readonly Query<IReadOnlyList<Todo>> _todos = new();

    protected override async Task OnInitializedAsync()
    {
        // DejaComponentBase already attached _todos — it re-renders this component on every
        // state change, and detaches (and cancels the fetch) when the component is disposed.
        // The key and the fetch are positional; the optional hook sets everything else.
        await _todos.Execute("todos", Api.GetTodosAsync, p =>
            p.OnError = _ => { }); // error state is rendered inline above
    }
}
```

And a write:

```csharp
private readonly Mutation<Todo> _addTodo = new();

private Task AddTodo() => _addTodo.Execute(() => Api.AddTodoAsync(_title), p =>
{
    // With AddDeja(): invalidates the key and refetches every mounted query under it,
    // in every component showing todos. (Without the cache, refetch manually via
    // OnSuccessAsync = _ => LoadTodos().)
    p.InvalidateKeys = [QueryKey.Of("todos")];
    p.OnDisplayUserError = e => _error = e.DisplayMessage;
});
```

Both `Execute` overloads are shorthand for the full form, which stays available whenever every
option needs to be set at once — `Execute(new QueryParameters<T> { … })` and
`Execute(new MutationParameters<T> { … })` behave exactly as before.

## Component binding

`DejaComponentBase` scans the component's own fields and properties once, at `OnInitialized`, and
attaches to every `Query<T>` and `Mutation<T>` it finds. Declare as many as you like — no
registration list, no keys, no ordering:

```razor
@inherits DejaComponentBase

@code {
    private readonly Query<Profile> _profile = new();
    private readonly Query<IReadOnlyList<Order>> _orders = new();
    private readonly Mutation<Order> _placeOrder = new();
}
```

**Overriding `OnInitialized`.** The base overrides `OnInitialized`, not `OnInitializedAsync`. If
you override `OnInitialized` yourself, call `base.OnInitialized()` or nothing is attached.
Overriding only `OnInitializedAsync` is unaffected.

**State created later.** Discovery runs once, so state assigned afterwards must be wrapped in
`Observe`, which returns its argument:

```csharp
private Query<Detail>? _detail;

private async Task OpenDetail(int id)
{
    _detail = Observe(new Query<Detail>());
    await _detail.Execute(new QueryParameters<Detail>
    {
        QueryFunction = token => Api.GetDetailAsync(id, token),
    });
}
```

Every observed instance is disposed with the component, so reopening a detail pane many times
leaks nothing — but each stays attached. For a pane opened repeatedly, reuse one field and
re-`Execute` it.

**One owner per instance.** Deja state notifies a single listener, so a second `Attach` throws.
`[Parameter]`, `[CascadingParameter]` and `[Inject]` members are never attached — the parent or
the container owns those. To share fetched data, give each component its own `Query<T>` on a
shared `QueryKey` (they share one cache entry and one fetch), or pass the data down:

```razor
<TodoList Items="_todos.Data ?? []" Busy="_todos.IsLoading" />
```

If a child needs to trigger a refetch, pass an `EventCallback` rather than the `Query<T>` itself.

The cache does not weaken the isolation mechanism: `IDejaObservable` stays single-listener, and a
`Query<T>` still notifies exactly one component. Two queries on the same key never talk to each
other — they each independently observe the cache entry, which is the multicast point.

Genuinely shared application state (a `CartService` several unrelated components render) is
deliberately out of scope: the single-listener rule is what buys the isolation guarantee. Use an
event on the service, or a library such as
[`CommunityToolkit.Mvvm`](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/).

## Manual refetch

`Refetch()` re-runs the query's last `Execute` and always fetches — staleness, `RefetchOnMount`
and `Enabled` are bypassed, because a user pressing a refresh button is an explicit request, not a
mount policy. On the cached path the result updates the shared entry, so every component on the
key re-renders; a same-key fetch already in flight is joined rather than duplicated. Before the
first `Execute` (or after disposal) it does nothing.

```razor
<button @onclick="() => _todos.Refetch()" disabled="@_todos.IsReFetching">Refresh</button>
```

To change something for one refresh, pass `RefetchParameters<T>` — the subset of
`QueryParameters<T>` a refetch may override: the callbacks, `CancellationToken`, `StaleTime` and
`CacheTime`. The key and fetch function always come from the last `Execute` — changing those is a
new query, not a refetch. Overrides are one-shot: a property left null keeps the remembered value,
a later bare `Refetch()` is unaffected.

```csharp
private Task RefreshTodos() => _todos.Refetch(new RefetchParameters<IReadOnlyList<Todo>>
{
    OnSuccess = _ => _toast.Show("Todos refreshed"),
});
```

Because Deja never runs queries on its own, `Refetch` is also the natural partner of a *lazy*
query: `Execute` with `Enabled = false` registers the parameters without fetching, and a later
`Refetch()` triggers the first actual fetch.

## Chaining dependent requests

When one request needs the result of another, run the second from the first's `OnSuccessAsync`.
The callback is awaited, so each step genuinely waits for the one before it, and the outer
`Execute` does not complete until the whole chain has:

```csharp
await _user.Execute("user", Api.GetUserAsync, p => p.OnSuccessAsync = async user =>
{
    if (user is null) return;

    await _orders.Execute(QueryKey.Of("orders", user.Id), t => Api.GetOrdersAsync(user.Id, t));

    // Each step sees the previous one settled, so an ordinary if is enough to branch
    if (_orders.Data is { Count: > 0 })
    {
        await _invoices.Execute(QueryKey.Of("invoices", user.Id), t => Api.GetInvoicesAsync(user.Id, t));
    }
});
```

Callbacks run on **every** completed `Execute`, including one that served fresh cached data
without fetching — a chain behaves the same whether the parent hit the cache or went to the
network. (`OnSuccess` needs a result to hand over, so a query that is `Enabled = false` with an
empty cache settles without reporting success.)

The same works from a mutation, which is the natural place to fan out into several reads:

```csharp
await _save.Execute(() => Api.SaveAsync(_dto), p => p.OnSuccessAsync = async saved =>
{
    await _todos.Refetch();

    if (saved!.AffectsStats)
    {
        await _stats.Refetch();
    }
});
```

For a plain "refresh these keys afterwards", prefer `InvalidateKeys` — it refetches every mounted
query on those keys, in every component, with no callback wiring. Reach for `OnSuccessAsync` when
the next request's *key or arguments* come from the previous result, which is what invalidation
cannot express. `InvalidateKeys` runs after `OnSuccess` and is awaited, so `OnSettled` sees the
refetches finished.

Three things worth knowing when a chain gets long:

- **Give every step its own `OnError`.** A failing query with no error callback throws
  `InvalidOperationException`, which propagates out of the parent's `OnSuccessAsync` into the
  parent's error handling — so the parent would report the *child's* failure as its own. With an
  error callback the failure stays where it happened.
- **The parent stays loading for the whole chain.** Because the callback is awaited inside the
  parent's execution, `_user.IsLoading` covers every step. Bind a spinner to the last query in the
  chain, or to the combination (`_user.IsLoading || _orders.IsLoading`).
- **Invalidation does not cascade.** Invalidating `user` refetches `user` and re-renders, but it
  does not re-run the callback chain — so `orders` keeps its previous data. Chains re-run when you
  call `Execute`, not when a parent entry is invalidated underneath them. Invalidate the dependent
  keys too if they need to follow.

## Cancellation

A component never owns a `CancellationTokenSource`. `DejaComponentBase` holds one lifetime token,
hands it to every `Query<T>` and `Mutation<T>` it discovers, and cancels it when the component is
disposed — so navigating away aborts the request (down to the server) with nothing at the call
site:

```razor
@inherits DejaComponentBase

@code {
    private readonly Query<IReadOnlyList<Todo>> _todos = new();

    // No token, no CancellationTokenSource, no Dispose override — navigating away
    // mid-fetch cancels this automatically.
    protected override Task OnInitializedAsync() => _todos.Execute("todos", Api.GetTodosAsync);
}
```

Mutations get the same treatment through their cancellation-aware overload:

```csharp
private Task AddTodo() => _addTodo.Execute(token => Api.AddTodoAsync(_title, token), p =>
    p.InvalidateKeys = [QueryKey.Of("todos")]);
```

**Work Deja doesn't run for you** — a direct API call from an event handler — can use the same
token via the protected `ComponentToken` property. There is nothing to dispose; the component owns
the source:

```csharp
private async Task Ping() => await Api.PingAsync(ComponentToken);
```

**Narrowing the scope.** Set `CancellationToken` on the parameters to tie an execution to something
shorter-lived than the component — a search box that abandons the previous keystroke, say. An
explicit token *replaces* the ambient one rather than combining with it; the query object is still
owned by the component, so disposal ends its work either way.

```csharp
await _results.Execute(new QueryParameters<SearchResults>
{
    QueryFunction = token => Api.SearchAsync(_term, token),
    CancellationToken = _searchCts.Token,
});
```

Outside a component (a `Query<T>` constructed with a `DejaClient` directly) there is no ambient
token, and leaving `CancellationToken` null means no caller-owned cancellation — unchanged from
before.

**On the cached path**, a caller's token detaches *that* caller without cancelling the shared
fetch: one component unmounting must not abort data another component is still waiting for. The
shared fetch is cancelled only when its last subscriber leaves.

## Documentation site

The repository contains a Blazor WebAssembly documentation site that dogfoods Deja itself: guides
for every feature, a hand-curated API reference, and live demos running against a real API —
including a shared-cache page with two sibling components on one cache entry, an isolation page
with two unkeyed siblings that cannot re-render one another, and an optimistic-write demo with
rollback:

```bash
dotnet run --project docs/Deja.Docs
```

## Roadmap

- Refetch on window focus / reconnect.
- Polling (`RefetchInterval`) and a general retry/backoff policy.
- Optimistic updates for mutations, prefetching, infinite/paged queries.
- Persistence to `localStorage` / `sessionStorage`.
- A separate, explicitly multicast store for shared application state.

## Development

```bash
dotnet build      # library + docs site + tests
dotnet test       # xUnit tests
dotnet format     # CI enforces formatting
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

Released under the [MIT license](LICENSE).
