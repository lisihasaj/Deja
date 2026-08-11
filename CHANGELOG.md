# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
