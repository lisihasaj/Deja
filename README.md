# Deja

**Data fetching, mutation and caching primitives for Blazor — WebAssembly and Server.**

Déjà vu: you have seen this data before, and Deja remembers it for you.

> **Status: early preview.** Deja is being extracted from a production Blazor application. The
> current `Query<T>` / `Mutation<T>` API mirrors that battle-tested code and will evolve toward a
> full client (central cache, invalidation, stale times) before 1.0. Expect
> breaking changes while the version is 0.x.

## What's here today

### `DejaComponentBase` — automatic re-rendering

- Inherit it and every `Query<T>` / `Mutation<T>` the component declares re-renders it
  automatically: no `INotifyPropertyChanged` subscription, no handler, no `IDisposable`.
- **Strict per-component isolation.** Deja state notifies exactly one listener — the component
  that owns it. Two components each holding their own `Query<T>` can never re-render each other.
- Disposing the component detaches from everything it observed and cancels its in-flight fetches.

### `Query<T>` — declarative async reads

- Bindable lifecycle state: `IsLoading`, `IsReFetching`, `IsError`,
  `ErrorMessage`, `Data`, `ReFetchCount` — one notification per state transition, so components
  re-render once per change rather than once per property.
- **Supersede & cancel**: a newer `Execute` cancels the in-flight one (all the way down to the
  server), and a slow stale response can never overwrite fresh data.
- **In-flight deduplication**: concurrent calls sharing a `QueryKey` join the same execution
  instead of hitting the server twice.
- **Cancellation-aware fetches**: Deja hands your `QueryFunction` a token that is cancelled on
  supersede or dispose; ignore it with `_ =>` when the fetch can't observe cancellation.
- **Timeout resilience**: an `HttpClient` timeout caused by a frozen background browser tab is
  retried once automatically (queries are idempotent reads).
- Success / error / settled callbacks, sync and async, plus dedicated callbacks for
  `DisplayUserException` — errors whose message is meant for the end user.

### `Mutation<T>` — declarative async writes

- Bindable `IsLoading`, `IsError`, `ErrorMessage`, `Data` state.
- Typed (`MutationFunction`) or void (`VoidMutationFunction`) mutations.
- Success / error / settled callbacks, sync and async, with `DisplayUserException` support.

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
        await _todos.Execute(new QueryParameters<IReadOnlyList<Todo>>
        {
            QueryKey = "todos",
            QueryFunction = Api.GetTodosAsync,
            OnError = _ => { } // error state is rendered inline above
        });
    }
}
```

And a write:

```csharp
private readonly Mutation<Todo> _addTodo = new();

private Task AddTodo() => _addTodo.Execute(new MutationParameters<Todo>
{
    MutationFunction = () => Api.AddTodoAsync(_title),
    OnSuccessAsync = _ => LoadTodos(), // refetch the query
    OnDisplayUserError = e => _error = e.DisplayMessage
});
```

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
the container owns those. To share fetched data, pass the data down rather than the query:

```razor
<TodoList Items="_todos.Data ?? []" Busy="_todos.IsLoading" />
```

If a child needs to trigger a refetch, pass an `EventCallback` rather than the `Query<T>` itself.

Genuinely shared application state (a `CartService` several unrelated components render) is
deliberately out of scope: the single-listener rule is what buys the isolation guarantee. Use an
event on the service, or a library such as
[`CommunityToolkit.Mvvm`](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/).

## Sample app

The repository contains a Blazor WebAssembly demo exercising every feature, including an
`/isolation` page showing two sibling components that cannot re-render one another:

```bash
dotnet run --project samples/Deja.Sample
```

## Roadmap

- Central `QueryClient` with a shared, keyed cache (serve cached data instantly, refetch in the
  background).
- Cache invalidation, stale-time / gc-time semantics, and query key hierarchies.
- Refetch on window focus / reconnect.
- Optimistic updates for mutations.
- DI registration helpers.
- A separate, explicitly multicast store for shared application state.

## Development

```bash
dotnet build      # library + sample + tests
dotnet test       # xUnit tests
dotnet format     # CI enforces formatting
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

Released under the [MIT license](LICENSE).
