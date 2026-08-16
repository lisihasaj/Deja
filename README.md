# Deja

**Data fetching, mutation and caching primitives for Blazor — WebAssembly and Server.**

📖 **[Documentation & live demos](https://lisihasaj.github.io/Deja/)** · 📦 **[NuGet](https://www.nuget.org/packages/Deja/)**

Déjà vu: you have seen this data before, and Deja remembers it for you.

> **Status: early preview.** Deja is being extracted from a production Blazor application.
> Expect breaking changes while the version is 0.x.

Deja does three things for Blazor: it removes the boilerplate around talking to an API, it caches
what you fetched, and it keeps that data synchronized across every component showing it. If you know
React Query (TanStack Query), the model will feel familiar — queries, mutations and a shared keyed
cache, in idiomatic C#.

```bash
dotnet add package Deja
```

Multi-targets `net8.0`, `net9.0` and `net10.0`. No third-party dependencies.

```csharp
// Program.cs — the shared cache is opt-in
builder.Services.AddDeja();
```

## What it replaces

### Boilerplate

Every component that fetches data hand-rolls the same machinery: a loading flag, a try/catch for the
error message, an `IDisposable` with a `CancellationTokenSource` so navigating away doesn't leave
requests running, and `StateHasChanged()` wherever a continuation might land.

**Before** — 20 lines of machinery around one call:

```razor
@implements IDisposable

@code {
    private List<Todo>? _todos;
    private bool _loading;
    private string? _error;
    private readonly CancellationTokenSource _cts = new();

    protected override async Task OnInitializedAsync()
    {
        _loading = true;
        try { _todos = await Api.GetTodosAsync(_cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception e) { _error = e.Message; }
        finally { _loading = false; StateHasChanged(); }
    }

    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}
```

**After** — the call, and nothing else:

```razor
@inherits DejaComponentBase

@code {
    private readonly Query<List<Todo>> _todos = new();

    protected override Task OnInitializedAsync()
        => _todos.Execute("todos", Api.GetTodosAsync);
}
```

No flag, no catch block, no token plumbing, no `StateHasChanged`. `IsLoading`, `IsError`,
`ErrorMessage` and `Data` are bindable state, and disposing the component cancels what's in flight.

### Prop drilling

Because that boilerplate is per component, the usual reaction is to fetch once in the page, drill the
data down as `[Parameter]`s, and thread an `EventCallback` back up so a child that changes something
can ask the page to reload. Components in between grow a parameter they never use and a callback they
only forward; move the child and the whole chain gets re-threaded.

Centralizing API communication in the cache removes the chain entirely:

```text
Before — the page owns the request so the children don't duplicate it

  Todos.razor            fetch + flag + error + CTS, plus a Reload for the children
   └─ TodoLayout         Todos="…" OnChanged="…"    forwards both, uses neither
       ├─ TodoToolbar    Todos="…" OnChanged="…"
       └─ TodoTable      Todos="…" OnChanged="…"    three levels down, still on parameters

After — each component asks for the key it cares about

  Todos.razor            no parameters, no callbacks
   └─ TodoLayout         a layout again
       ├─ TodoToolbar
       └─ TodoTable      Query<List<Todo>> on "todos" — deduped into one request
```

Nothing is passed and nothing is called back. Three components asking for the same key cost one
request, and a mutation that names the key it invalidates refetches every component holding it —
including ones several levels away it has never heard of. Data placement stops being an architectural
decision: a component depends on a key, not on an ancestor.

## Quick start

```razor
@inherits DejaComponentBase
@inject TodoApi Api

@if (_todos.IsLoading) { <Spinner/> }
@if (_todos.IsError) { <Alert>@_todos.ErrorMessage</Alert> }
<ul>
    @foreach (var todo in _todos.Data ?? []) { <li>@todo.Title</li> }
</ul>

@code {
    private readonly Query<List<Todo>> _todos = new();
    private readonly Mutation<Todo> _add = new();

    protected override Task OnInitializedAsync()
        => _todos.Execute("todos", Api.GetTodosAsync);

    private Task Add() => _add.Execute(t => Api.AddTodoAsync(_title, t), p =>
        p.InvalidateKeys = [QueryKey.Of("todos")]);
}
```

## Documentation

The [docs site](https://lisihasaj.github.io/Deja/) is a Blazor WebAssembly app that dogfoods Deja
itself — guides, a curated API reference, and live demos running against a real API with controls to
inject latency and failures. Run it locally with `dotnet run --project docs/Deja.Docs`.

### Getting started

| Page | Summary |
|---|---|
| [Introduction](https://lisihasaj.github.io/Deja/getting-started/introduction) | Why Deja exists: the per-component boilerplate, the prop drilling it leads to, and the three things Deja does instead — no boilerplate, caching, synchronization. |
| [Installation](https://lisihasaj.github.io/Deja/getting-started/installation) | One package, one optional `AddDeja()` registration. Target frameworks, and why `DejaClient` stays **Scoped** on Blazor Server. |
| [Quick start](https://lisihasaj.github.io/Deja/getting-started/quick-start) | Zero to a cached, cancellable, auto-rendering query in three steps: execute it, bind its state, then write with a mutation and invalidate the key. |

### Live demos

| Page | Summary |
|---|---|
| [Todo list](https://lisihasaj.github.io/Deja/demos/todo-list) | Full CRUD against a live API: a keyed query for the list, three mutations for create, update and delete, and `SetData` applying results to the shared cache. |
| [Shared cache](https://lisihasaj.github.io/Deja/demos/shared-cache) | Two sibling components on one key. One fetch serves both, a refetch from either updates both, and the inspector shows the single entry they subscribe to. |
| [Isolation](https://lisihasaj.github.io/Deja/demos/isolation) | Two unkeyed siblings that provably cannot re-render each other — Deja state notifies exactly one listener. |
| [Optimistic write](https://lisihasaj.github.io/Deja/demos/optimistic-write) | Flip the UI immediately with `SetData`'s updater form, run the request after, and roll back to a snapshot if it fails. |

### Guides

| Page | Summary |
|---|---|
| [Queries](https://lisihasaj.github.io/Deja/guides/queries) | `Query<T>` tracks one async read as bindable state. The three `Execute` overloads, the state surface, `Enabled` for dependent queries, `Select` and `PlaceholderData`. |
| [Mutations](https://lisihasaj.github.io/Deja/guides/mutations) | `Mutation<T>` gives writes the same bindable treatment as reads. Four function shapes (typed, void, and their cancellable counterparts), `InvalidateKeys`, and chaining follow-up requests. |
| [Component base](https://lisihasaj.github.io/Deja/guides/component-base) | How `DejaComponentBase` discovers the state a component owns, the single-owner rule, `Observe` for state created later, `ComponentToken`, and cleanup. |
| [Rendering & re-renders](https://lisihasaj.github.io/Deja/guides/rendering) | The render contract: one notification per transition, a transition that changes nothing renders nothing, concurrent requests share a queued render, and renders stay inside the owning component. |
| [The cache](https://lisihasaj.github.io/Deja/guides/cache) | What a keyed `Execute` does, instant renders from cache with background revalidation, one fetch per key app-wide, entry lifecycle and eviction, and reading or writing entries directly. |
| [Query keys](https://lisihasaj.github.io/Deja/guides/query-keys) | Building structured, ordered keys with `QueryKey.Of`, canonical form and value equality, dictionaries normalising filter objects, prefix matching, and `IQueryKeySegment`. |
| [Cancellation](https://lisihasaj.github.io/Deja/guides/cancellation) | The ambient component token, per-call tokens that replace it, supersede-and-cancel, why the cached path detaches a caller instead of aborting a shared fetch, and `ComponentToken` for your own calls. |
| [Refetching & staleness](https://lisihasaj.github.io/Deja/guides/refetching) | Stale time, `RefetchOnMount`, manual `Refetch()` that always fetches, one-shot `RefetchParameters<T>` overrides, and the precedence of defaults. |
| [Error handling](https://lisihasaj.github.io/Deja/guides/error-handling) | Bindable error state, `DisplayUserException` for end-user messages, callback order, the unhandled-failure contract, why cancellation is not an error, and the timeout retry. |

### API reference

Curated reference for every public type: [`Query<T>`](https://lisihasaj.github.io/Deja/api/query),
[`QueryParameters<T>`](https://lisihasaj.github.io/Deja/api/query-parameters),
[`RefetchParameters<T>`](https://lisihasaj.github.io/Deja/api/refetch-parameters),
[`Mutation<T>`](https://lisihasaj.github.io/Deja/api/mutation),
[`MutationParameters<T>`](https://lisihasaj.github.io/Deja/api/mutation-parameters),
[`DejaClient`](https://lisihasaj.github.io/Deja/api/deja-client),
[`QueryKey`](https://lisihasaj.github.io/Deja/api/query-key),
[`DejaOptions`](https://lisihasaj.github.io/Deja/api/deja-options),
[`DejaComponentBase`](https://lisihasaj.github.io/Deja/api/deja-component-base) and the
[smaller enums and options](https://lisihasaj.github.io/Deja/api/enums).

## Roadmap

- Refetch on window focus / reconnect.
- Polling (`RefetchInterval`) and a general retry/backoff policy.
- Prefetching and infinite/paged queries.
- Persistence to `localStorage` / `sessionStorage`.
- A separate, explicitly multicast store for shared application state.

## Development

```bash
dotnet build      # library + docs site + tests
dotnet test       # xUnit tests
dotnet format     # CI enforces formatting
```

See [CONTRIBUTING.md](https://github.com/lisihasaj/Deja/blob/main/CONTRIBUTING.md) for guidelines.

## License

Released under the [MIT license](https://github.com/lisihasaj/Deja/blob/main/LICENSE).
