# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `Query<T>.Refetch(RefetchParameters<T>? parameters = null)`: re-runs the last `Execute` with a
  forced fresh fetch — staleness, `RefetchOnMount` and `Enabled` are bypassed, since a manual
  refetch is an explicit request rather than a mount policy. On the cached path the result updates
  the shared entry (every component on the key re-renders) and a same-key fetch already in flight
  is joined rather than duplicated. Before the first `Execute` or after disposal it is a no-op.
- `RefetchParameters<T>`: the subset of `QueryParameters<T>` a refetch may override — the
  success/error/settled callbacks, `CancellationToken`, `StaleTime` and `CacheTime`. The key and
  fetch function always come from the last `Execute`. Overrides apply to that call only; a
  property left null keeps the remembered value.

### Changed (error handling)

- **Breaking:** `Mutation<T>.Execute` no longer rethrows unconditionally on failure. It now matches
  the contract `Query<T>` already had: when an error callback (`OnError`, `OnErrorAsync`,
  `OnDisplayUserError`, `OnDisplayUserErrorAsync`) observed the failure, it is considered handled
  and nothing escapes; with no error callback wired, the wrapped
  `InvalidOperationException("Mutation failed", e)` is still thrown so an unobserved failure is
  never lost silently. Previously every caller had to wrap `await Execute(...)` in a `try/catch`
  purely to swallow a rethrow that arrived after the callbacks had already surfaced the error —
  and forgetting it turned an ordinary failed write into an unhandled render exception that tore
  down the component. Migration: delete the `catch (InvalidOperationException)` around calls that
  already pass an error callback; if a call site relied on the throw for control flow, keep it and
  drop the error callback (or rethrow from within it).

### Changed (cancellation)

- **Breaking:** `QueryParameters<T>.CancellationToken` is now `CancellationToken?`. Null (the
  default) means "not set", which is what lets a query fall back to the owning component's
  lifetime token; a value set here still replaces it. Assigning the property is unchanged —
  `CancellationToken = cts.Token` compiles as before — so this only breaks code that *reads* it,
  which migrates with `parameters.CancellationToken ?? CancellationToken.None`.

### Fixed (cancellation)

- `Query<T>.ClearData(cancelCurrentRequest: true)` was a no-op on the cached path: it cancelled
  `_executionCts`, which a cached execution does not have, so the in-flight execution still
  completed and ran its success/settled callbacks over state the caller had just reset. It now
  retires the cached generation as well — the cached-path analogue of cancelling the token. The
  shared cache entry is deliberately untouched (other components may be waiting on that fetch), so
  data still arrives through the entry subscription; use `DejaClient.Remove` to drop the entry
  itself.

### Changed (structure)

- **Breaking:** `AddDeja()` moved from the `Deja.DependencyInjection` namespace to `Deja`. A single
  `using Deja;` now covers the whole library. Migration: delete `using Deja.DependencyInjection;`
  from `Program.cs` and make sure `using Deja;` is present.
- Source layout flattened from 20 files to 9. The `Cache/` folder is gone — every type in it already
  declared `namespace Deja`, so the folder was invisible to consumers and only split related types
  apart. Small option and marker types now live in the file of the type that consumes them
  (`RefetchOnMount` with `Query<T>`, `QueryFilter`/`InvalidateOptions`/`RefetchType` with
  `DejaClient`, `QueryDefaults` with `DejaOptions`, and so on). No type was renamed and no public
  type changed accessibility; behaviour is unchanged.

### Added

- Automatic component-lifetime cancellation. `DejaComponentBase` owns one `CancellationTokenSource`,
  hands its token to every `Query<T>` and `Mutation<T>` it discovers, and cancels it at the top of
  disposal — before the derived cleanup hooks run, so they can observe it. A component no longer
  declares a `CancellationTokenSource`, threads a token through its call sites, or overrides
  `Dispose` to tear one down: `_todos.Execute("todos", Api.GetTodosAsync)` is scoped to the
  component's lifetime as written. The token is exposed as the protected `ComponentToken` for work
  Deja does not run (a direct API call from an event handler); it is created on first use, so a
  component that never reaches for cancellation allocates nothing, and reading it after disposal
  returns an already-cancelled token rather than throwing.
- Cancellation-aware mutations: `MutationParameters<T>.CancellableMutationFunction` and
  `CancellableVoidMutationFunction`, plus an `Execute(Func<CancellationToken, Task<T>>, …)`
  shorthand. Mutations previously could not be cancelled at all, so an in-flight write always
  outlived the component that started it. Cancellation is not a failure: it sets no error state,
  runs no callbacks, and throws nothing at the caller — matching `Query<T>`. A mutation started
  after disposal does not run.

- Shorthand `Execute` overloads on `Query<T>` and `Mutation<T>`, so the common call no longer
  restates the generic argument: `_todos.Execute("todos", Api.GetTodosAsync)` and
  `_addTodo.Execute(() => Api.AddTodoAsync(title), p => p.InvalidateKeys = [QueryKey.Of("todos")])`.
  An optional `configure` callback sets callbacks, `StaleTime`, `Select` and the rest. Purely
  additive — both wrap the existing `QueryParameters<T>` / `MutationParameters<T>` form, which
  stays the full-control path.
- `DejaClient`, the shared query cache. Registered with `AddDeja()` (in the `Deja`
  namespace) as a **Scoped** service — one cache per browser tab on
  WebAssembly, one per user circuit on Blazor Server (a singleton on Server would leak one user's
  cached responses to every other user). Keyed queries in `DejaComponentBase` components pick the
  client up automatically; it can also be supplied via the `Query<T>`/`Mutation<T>` constructors
  or per call via `QueryParameters<T>.Client` / `MutationParameters<T>.Client`. Entirely opt-in:
  without `AddDeja()` (or without a `QueryKey`) every query behaves exactly as before.
- Cached execution semantics for keyed queries: cached data publishes instantly
  (`IsCachedData`), staleness (`StaleTime`, default **zero** — cached data always revalidates in
  the background on the next mount) decides whether to refetch, and concurrent same-key
  executions from *any* component join one shared in-flight fetch. A successful fetch writes the
  cache entry, and every subscribed query — in every component — updates through it. Each
  `Execute` call still runs its own success/error/settled callbacks; passive updates (another
  component's refetch, `SetData`, invalidation) update `Data` without running callbacks. The
  caller's `CancellationToken` detaches that caller without cancelling the shared fetch; the
  fetch is cancelled when its last subscriber goes away.
- `QueryKey`: a structured, ordered, hierarchical cache key with a canonical, dependency-free,
  trimming/AOT-safe hash (no reflection, no JSON serializer). Segment order matters; dictionary
  segments are normalized by sorting keys; strings are quoted/escaped so `["a,b"]` and
  `["a","b"]` cannot collide. Custom segment types implement `IQueryKeySegment`; anonymous types
  are rejected at construction with guidance (use a `Dictionary<string, object?>`).
- Invalidation: `InvalidateAsync` (prefix-matched by default, `Exact`/`Predicate`/`RefetchType`
  options), `InvalidateAllAsync`, `RefetchAsync`, plus manual cache access with `GetData`,
  `TryGetData`, `SetData` (value and updater forms), `Remove`, `Clear` and `GetState`.
- `MutationParameters<T>.InvalidateKeys`: after a successful mutation (and after `OnSuccess`),
  the listed keys are invalidated and every mounted query under them refetches in the background
  — replacing manual `OnSuccessAsync = _ => Reload()` wiring and updating every component
  showing the data, not just the mutating one.
- Configuration: `DejaOptions` (`DefaultStaleTime`, `DefaultCacheTime`, `DefaultRefetchOnMount`,
  `MaxEntries`, `EvictionInterval`, `StructuralComparison`, `TimeProvider`), per-query overrides
  on `QueryParameters<T>` (`StaleTime`, `CacheTime`, `RefetchOnMount`, `Enabled`, `Select`,
  `PlaceholderData`), and per-prefix defaults via `DejaClient.SetDefaults` — precedence is
  per-query value, then longest matching prefix, then `DejaOptions`, regardless of registration
  order.
- Eviction: entries with no subscribers are removed after their `CacheTime` (default 5 minutes)
  by a periodic sweep; resubscribing before the deadline cancels the eviction (the fast
  back-navigation case), subscribed entries are never evicted, and `MaxEntries` adds an optional
  least-recently-used cap.
- New bindable properties on `Query<T>`: `UpdatedAt`, `IsCachedData` and `IsStale`.

### Changed (cache)

- **Breaking (binary):** `QueryParameters<T>.QueryKey` changes type from `string?` to the new
  `QueryKey?`. Source-compatible for string literals via an implicit conversion —
  `QueryKey = "todos"` still compiles and means `QueryKey.Of("todos")`; null/whitespace strings
  still mean "no key". Recompile against this version.
- In-flight deduplication for keyed queries moves from the query instance to the cache entry
  when a client is present, deduplicating across every component in the app — and making the key
  honestly the identity of the data (the documented caveat about a same-key join dropping the
  joining call's parameters now only applies to the no-client path, which keeps the old
  per-instance behavior unchanged).

### Added

- `DejaComponentBase`, a Blazor component base that re-renders automatically when the `Query<T>`
  and `Mutation<T>` instances it declares change state. Inherit it and the subscription, the
  change handler and the `IDisposable` implementation all disappear. Fields and properties are
  discovered at `OnInitialized`; state created later is registered with `Observe(...)`.
  `[Parameter]`, `[CascadingParameter]` and `[Inject]` members are deliberately never attached —
  they are owned by the parent or the container.
- `DejaComponentBase` routes all component cleanup through two symmetric hooks:
  `protected override void Dispose()` for synchronous cleanup and
  `protected override ValueTask DisposeAsync()` for asynchronous cleanup. Both run during
  disposal — sync first, then async — before the base detaches from observed state and disposes
  the queries the component owns. The base implements `IAsyncDisposable` explicitly (Blazor
  disposes through the interface, and the explicit implementation keeps the natural name free
  for the override) and deliberately not `IDisposable`: Blazor never calls `Dispose` once
  `IAsyncDisposable` is present, so the interface would gain nothing from the framework while
  inviting a manual synchronous disposal that skips the asynchronous cleanup. The one rule:
  never declare `IDisposable` or `IAsyncDisposable` on a derived component — a redeclared
  `DisposeAsync` would replace the base's disposal and skip all Deja cleanup, and a redeclared
  `Dispose` would never be called by anything. Neither mistake fails silently: the constructor
  detects a redeclared parameterless `Dispose` or `DisposeAsync` (on the component or any base
  type) and throws an `InvalidOperationException` that spells out the override to use instead.
- `DejaComponentBase` detects an `OnInitialized` override that forgets to call
  `base.OnInitialized()` — the one mistake that silently disables attachment — and reports it
  once per component as a console error (`console.error` in the browser on WebAssembly, stderr
  on the server) on first render.
- `IDejaObservable` and `DejaObservable`: a single-listener attachment contract replacing the
  multicast `INotifyPropertyChanged` protocol. `Attach(Action)` returns a handle that detaches on
  dispose, and a second concurrent `Attach` throws. This is what guarantees per-component
  isolation — two components each holding their own `Query<T>` can never re-render each other.

### Changed

- **Breaking:** `QueryParameters<T>.QueryFunction` and `QueryFunctionWithToken` are merged into a
  single `QueryFunction` of type `Func<CancellationToken, Task<T>>`, removing the two-property
  surface and its precedence rule. Migration: rename `QueryFunctionWithToken` to `QueryFunction`
  (the signature is unchanged); for a fetch that ignores cancellation, discard the token —
  `QueryFunction = () => FetchAsync()` becomes `QueryFunction = _ => FetchAsync()`.
- **Breaking:** `Query<T>` and `Mutation<T>` no longer implement `INotifyPropertyChanged`; they
  now derive from `DejaObservable`. Migration: delete the `PropertyChanged += / -=` lines and the
  handler, and inherit `DejaComponentBase` in the component.
- One notification per state transition instead of one per property. A full `Query<T>.Execute`
  now notifies about three times rather than about six, and it is no longer possible to mutate a
  property and forget its matching raise.
- Notifications are raised before success/error callbacks run, so a listener never renders
  against data the query has accepted but not yet published.
- `Deja` now depends on `Microsoft.AspNetCore.Components` for `DejaComponentBase`.
  `IDejaObservable`, `DejaObservable`, `Query<T>` and `Mutation<T>` remain pure BCL.

### Removed

- **Breaking:** the `PropertyChanged` event and the `protected OnPropertyChanged` method on
  `Query<T>` and `Mutation<T>`.
- **Breaking:** `Query<T>.InitialLoading`. Migration: bind `IsLoading` instead. Note the
  behavioural difference — `InitialLoading` was true only for the first execution, so a guard
  that swapped the data out for a skeleton now does so on every refetch as well. To keep the
  first-load-only behaviour, test `IsLoading && ReFetchCount == 1`; to keep showing data during a
  refetch, pair `IsLoading` with `IsReFetching`.

### Fixed

- `Query<T>.ClearData` reset `IsLoading`, `IsError`, `ErrorMessage`, `ReFetchCount`,
  `InitialLoading` and `IsReFetching` while notifying only for `Data`, so a component binding
  `IsError` kept rendering an error that had already been cleared. The whole reset is now covered
  by a single notification.
- `Mutation<T>.Execute` cleared a previous `IsError` / `ErrorMessage` on entry without notifying;
  the cleared error now rides the entry transition's notification.

### Added (initial extraction)

- Initial extraction of the `Query<T>` and `Mutation<T>` primitives:
  - `Query<T>` with bindable `InitialLoading` / `IsLoading` / `IsReFetching` / `IsError` /
    `ErrorMessage` / `Data` / `ReFetchCount` state, in-flight deduplication by `QueryKey`,
    supersede-and-cancel of stale executions, cancellation-aware fetch functions, a one-shot
    retry for browser-tab-freeze `HttpClient` timeouts, and success / error / settled callbacks.
  - `Mutation<T>` with bindable `IsLoading` / `IsError` / `ErrorMessage` / `Data` state and
    success / error / settled callbacks, including typed and `void` mutation functions.
  - `DisplayUserException` for errors whose message is safe to show to the end user, routed to
    dedicated `OnDisplayUserError` callbacks.
