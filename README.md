# Deja

**React Query-style data fetching, mutation and caching primitives for Blazor — WebAssembly and Server.**

Déjà vu: you have seen this data before, and Deja remembers it for you.

> **Status: early preview.** Deja is being extracted from a production Blazor application. The
> current `Query<T>` / `Mutation<T>` API mirrors that battle-tested code and will evolve toward a
> full React Query-style client (central cache, invalidation, stale times) before 1.0. Expect
> breaking changes while the version is 0.x.

## What's here today

### `Query<T>` — declarative async reads

- Bindable lifecycle state: `InitialLoading`, `IsLoading`, `IsReFetching`, `IsError`,
  `ErrorMessage`, `Data`, `ReFetchCount` — with `INotifyPropertyChanged` so components re-render.
- **Supersede & cancel**: a newer `Execute` cancels the in-flight one (all the way down to the
  server), and a slow stale response can never overwrite fresh data.
- **In-flight deduplication**: concurrent calls sharing a `QueryKey` join the same execution
  instead of hitting the server twice.
- **Cancellation-aware fetches**: pass a `QueryFunctionWithToken` and Deja hands it a token that
  is cancelled on supersede or dispose.
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
@implements IDisposable
@inject TodoApi Api

@if (_todos.InitialLoading)
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
        _todos.PropertyChanged += (_, _) => _ = InvokeAsync(StateHasChanged);

        await _todos.Execute(new QueryParameters<IReadOnlyList<Todo>>
        {
            QueryKey = "todos",
            QueryFunctionWithToken = Api.GetTodosAsync,
            OnError = _ => { } // error state is rendered inline above
        });
    }

    public void Dispose() => _todos.Dispose();
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

## Sample app

The repository contains a Blazor WebAssembly demo exercising every feature:

```bash
dotnet run --project samples/Deja.Sample
```

## Roadmap

- Central `QueryClient` with a shared, keyed cache (serve cached data instantly, refetch in the
  background).
- Cache invalidation, stale-time / gc-time semantics, and query key hierarchies.
- Refetch on window focus / reconnect.
- Optimistic updates for mutations.
- DI registration and Razor component helpers.

## Development

```bash
dotnet build      # library + sample + tests
dotnet test       # xUnit tests
dotnet format     # CI enforces formatting
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

Released under the [MIT license](LICENSE).
