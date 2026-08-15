# Deja public API inventory

Working artifact for the docs site: every public type and member, extracted from source with the
`file:line` it came from. The API reference pages are written against this checklist. Source of
truth is `src/Deja/*.cs`; line numbers are as of the commit this file was authored on.

## `Query<T>` — src/Deja/Query.cs:17

Bindable state:

| Member | Line | Notes |
|---|---|---|
| `bool IsLoading` | Query.cs:75 | true while any execution is loading |
| `bool IsError` | Query.cs:78 | cleared by a successful refetch |
| `string? ErrorMessage` | Query.cs:81 | |
| `T? Data` | Query.cs:84 | |
| `int ReFetchCount` | Query.cs:87 | increments on every execution, including the first |
| `bool IsReFetching` | Query.cs:90 | executions after the first (uncached), or when cached data exists (cached) |
| `DateTimeOffset? UpdatedAt` | Query.cs:96 | cached path only; null on the uncached path |
| `bool IsCachedData` | Query.cs:102 | true while Data came from the cache and no fresh fetch completed |
| `bool IsStale` | Query.cs:108 | computed: entry invalidated or older than effective stale time; always false uncached |

Constructors and methods:

| Member | Line | Notes |
|---|---|---|
| `Query()` | Query.cs:42 | resolves client from owning component |
| `Query(DejaClient)` | Query.cs:48 | throws `ArgumentNullException` on null |
| `Task Execute(QueryKey?, Func<CancellationToken, Task<T>>, Action<QueryParameters<T>>?)` | Query.cs:127 | keyed shorthand; throws `ArgumentNullException` when queryFunction is null |
| `Task Execute(Func<CancellationToken, Task<T>>, Action<QueryParameters<T>>?)` | Query.cs:152 | unkeyed shorthand (delegates with `key: null`) |
| `Task Execute(QueryParameters<T>)` | Query.cs:167 | full form; no-op on null parameters or after dispose |
| `Task Refetch(RefetchParameters<T>?)` | Query.cs:188 | forces fetch: clones last parameters, sets `Enabled = true`, `RefetchOnMount = Always`; no-op before first Execute / after dispose |
| `void ClearData(bool cancelCurrentRequest)` | Query.cs:606 | also retires the cached generation so an in-flight cached await is superseded |
| `void ClearData()` | Query.cs:631 | resets view only; observed cache entry untouched |
| `void Dispose()` | Query.cs:651 | cancels in-flight execution; unsubscribes from entry (may cancel entry fetch if last subscriber) |
| `protected virtual void Dispose(bool)` | Query.cs:658 | standard dispose pattern |

Behavioural facts to document (all verified in source):

- Unkeyed path: newer `Execute` supersedes and cancels the older in-flight one (Query.cs:429–447).
- Keyed without client: same-key concurrent call **joins** the in-flight execution; its parameters
  are dropped and no supersede-cancel happens (caution comment, Query.cs:217–222).
- Keyed with client (cached path): entry is the multicast point; success never writes
  `Query<T>.Data` directly, it writes the entry (Query.cs:239–242).
- Caller token is *not* linked into the shared fetch; `WaitAsync` detaches the caller without
  cancelling the request (Query.cs:311–314).
- Unhandled failure throws `InvalidOperationException("Query failed", inner)` **only when no error
  callback observed it** (Query.cs:344, Query.cs:505).
- `PlaceholderData` rendered only while cache empty and first fetch runs; never written to the
  cache (Query.cs:281–286).
- Invalidation trumps `RefetchOnMount.Never` (Query.cs:369–370).
- Timeout retry: single retry via `TimeoutRetry` (Query.cs:544).
- Callback order on success: `OnSuccessAsync`, then `OnSuccess` (Query.cs:547–555); on error:
  `OnDisplayUserErrorAsync`, `OnDisplayUserError` (only for `DisplayUserException`), `OnErrorAsync`,
  `OnError` (Query.cs:558–590); settled last, not on cancellation (Query.cs:592–600).

## `QueryParameters<T>` — src/Deja/Query.cs:686

18 public properties:

| Property | Line |
|---|---|
| `QueryKey? QueryKey` | Query.cs:693 |
| `Func<CancellationToken, Task<T>>? QueryFunction` | Query.cs:701 |
| `CancellationToken? CancellationToken` | Query.cs:712 (replaces, never combines with, the component token — Query.cs:71–72) |
| `Func<T?, Task>? OnSuccessAsync` | Query.cs:715 |
| `Action<T?>? OnSuccess` | Query.cs:718 |
| `Func<Exception, Task>? OnErrorAsync` | Query.cs:721 |
| `Action<Exception>? OnError` | Query.cs:724 |
| `Func<DisplayUserException, Task>? OnDisplayUserErrorAsync` | Query.cs:727 |
| `Action<DisplayUserException>? OnDisplayUserError` | Query.cs:730 |
| `Func<T?, Task>? OnSettledAsync` | Query.cs:733 |
| `Action<T?>? OnSettled` | Query.cs:736 |
| `DejaClient? Client` | Query.cs:743 |
| `TimeSpan? StaleTime` | Query.cs:746 |
| `TimeSpan? CacheTime` | Query.cs:749 |
| `RefetchOnMount? RefetchOnMount` | Query.cs:752 |
| `bool? Enabled` | Query.cs:759 (cached path only; null means enabled) |
| `Func<T, T>? Select` | Query.cs:766 (per query projection; cache stores raw value) |
| `T? PlaceholderData` | Query.cs:772 |

## `RefetchParameters<T>` — src/Deja/Query.cs:805

One-shot override subset — deliberately no key, fetch function, or client (Query.cs:798–803):
`CancellationToken` (:808), `OnSuccessAsync` (:811), `OnSuccess` (:814), `OnErrorAsync` (:817),
`OnError` (:820), `OnDisplayUserErrorAsync` (:823), `OnDisplayUserError` (:826), `OnSettledAsync`
(:829), `OnSettled` (:832), `StaleTime` (:838 — the refetch always fetches; this changes only how
soon the fresh result goes stale), `CacheTime` (:841).

## `RefetchOnMount` — src/Deja/Query.cs:863

`Never` (:866 — invalidation still refetches), `IfStale` (:869), `Always` (:872). A key with no
cached data always fetches regardless (Query.cs:860–861).

## `Mutation<T>` — src/Deja/Mutation.cs:13

| Member | Line | Notes |
|---|---|---|
| `Mutation()` | Mutation.cs:16 | |
| `Mutation(DejaClient)` | Mutation.cs:22 | throws `ArgumentNullException` on null |
| `bool IsLoading` | Mutation.cs:45 | |
| `bool IsError` | Mutation.cs:48 | |
| `string? ErrorMessage` | Mutation.cs:51 | |
| `T? Data` | Mutation.cs:54 | result of most recent successful execution |
| `Task Execute(Func<Task<T>>, Action<MutationParameters<T>>?)` | Mutation.cs:69 | shorthand |
| `Task Execute(Func<CancellationToken, Task<T>>, Action<MutationParameters<T>>?)` | Mutation.cs:92 | cancellation-aware shorthand |
| `Task Execute(MutationParameters<T>)` | Mutation.cs:112 | full form |

Behavioural facts:

- Pre-cancelled token (component already disposed) → returns without running (Mutation.cs:117–119).
- Function precedence: `CancellableMutationFunction` > `MutationFunction` >
  `CancellableVoidMutationFunction` > `VoidMutationFunction`; none set →
  `ArgumentException` (Mutation.cs:181–209).
- Only value-returning shapes write `Data`; a void mutation leaves the previous result standing
  (Mutation.cs:179–180). On failure `Data` is reset to default (Mutation.cs:152).
- `InvalidateKeys` runs **after** `OnSuccess`, prefix-matched, awaiting each
  `client.InvalidateAsync(key)`; ignored without a client (Mutation.cs:140–142, 213–226).
- Unhandled failure throws `InvalidOperationException("Mutation failed", inner)` only when no
  error callback was supplied (Mutation.cs:107–110, 157–165).
- Cancellation is not a failure: no error state, no callbacks, settled callbacks skipped
  (Mutation.cs:144–149, 172–175).

## `MutationParameters<T>` — src/Deja/Mutation.cs:286

`MutationFunction` (:289), `VoidMutationFunction` (:292), `CancellableMutationFunction` (:299),
`CancellableVoidMutationFunction` (:305), `CancellationToken` (:313 — replaces the ambient token),
`OnSuccess` (:316), `OnSuccessAsync` (:319), `OnErrorAsync` (:322), `OnError` (:325),
`OnDisplayUserErrorAsync` (:328), `OnDisplayUserError` (:331), `OnSettled` (:334),
`OnSettledAsync` (:337), `Client` (:344), `IReadOnlyList<QueryKey>? InvalidateKeys` (:351).

## `DejaClient` — src/Deja/DejaClient.cs:21

| Member | Line | Notes |
|---|---|---|
| `DejaClient(DejaOptions?)` | DejaClient.cs:35 | starts eviction loop |
| `T? GetData<T>(QueryKey)` | DejaClient.cs:52 | default when absent / no data / type mismatch |
| `bool TryGetData<T>(QueryKey, out T?)` | DejaClient.cs:60 | type mismatch → false + debug warning, never throws |
| `CacheEntryState? GetState(QueryKey)` | DejaClient.cs:84 | read-only snapshot, null when absent |
| `void SetData<T>(QueryKey, T)` | DejaClient.cs:97 | creates entry; throws `InvalidOperationException` on type mismatch |
| `void SetData<T>(QueryKey, Func<T?, T>)` | DejaClient.cs:109 | updater receives current data or default |
| `Task InvalidateAsync(QueryKey, InvalidateOptions?)` | DejaClient.cs:127 | prefix by default; task completes when triggered refetches settle |
| `Task InvalidateAllAsync()` | DejaClient.cs:136 | RefetchType.Active |
| `Task RefetchAsync(QueryKey, QueryFilter?)` | DejaClient.cs:143 | regardless of staleness; failures recorded, not thrown |
| `void Remove(QueryKey, QueryFilter?)` | DejaClient.cs:159 | cancels in-flight fetches; subscribed queries keep rendering last data |
| `void Clear()` | DejaClient.cs:170 | |
| `void SetDefaults(QueryKey, QueryDefaults)` | DejaClient.cs:193 | per-prefix defaults; longest prefix wins, order-independent |
| `void Dispose()` | DejaClient.cs:210 | |

Internals worth documenting as behaviour: entry type mismatch on `GetOrCreateEntry` throws
(DejaClient.cs:236–241); eviction sweep drops zero-subscriber entries past `EvictAt`
(DejaClient.cs:259–276); `MaxEntries` evicts least-recently-used zero-subscriber entries, never
subscribed ones (DejaClient.cs:336–362).

Defaults precedence (DejaClient.cs:188–192, 248–255): per-query value > longest matching prefix
defaults > `DejaOptions`.

## `QueryFilter` — src/Deja/DejaClient.cs:388

`bool Exact` (:394, default false = prefix), `Func<QueryKey, bool>? Predicate` (:397).

## `InvalidateOptions` — src/Deja/DejaClient.cs:401

`bool Exact` (:408), `Func<QueryKey, bool>? Predicate` (:411),
`RefetchType RefetchType = RefetchType.Active` (:414).

## `RefetchType` — src/Deja/DejaClient.cs:418

`Active` (:424), `All` (:427), `None` (:430).

## `ServiceCollectionExtensions.AddDeja` — src/Deja/DejaClient.cs:449

Registers `DejaOptions` singleton + `DejaClient` **Scoped** (DejaClient.cs:456–457). Scoped is
deliberate: per-tab on WASM, per-circuit on Server; a singleton on Server would leak one user's
cached responses to every other user (remarks, DejaClient.cs:442–448).

## `QueryKey` / `IQueryKeySegment` — src/Deja/QueryKey.cs:30, :312

| Member | Line | Notes |
|---|---|---|
| `static QueryKey Of(params object?[])` | QueryKey.cs:62 | throws on null/empty/unsupported segment; clones the array |
| `static QueryKey? FromString(string?)` | QueryKey.cs:81 | null/whitespace → null (no key) |
| `implicit operator QueryKey?(string?)` | QueryKey.cs:88 | |
| `bool StartsWith(QueryKey)` | QueryKey.cs:109 | segment-wise on canonical forms; `["todo"]` does not match `["todos"]` |
| `bool Equals(QueryKey?)` / `==` / `!=` / `GetHashCode` | QueryKey.cs:130–144 | value equality on canonical form |
| `string ToString()` | QueryKey.cs:147 | canonical form, e.g. `["todos",{"page":2}]` |
| `IQueryKeySegment.ToKeySegment()` | QueryKey.cs:319 | must be stable and deterministic |

Segment rules (QueryKey.cs:149–221): strings quoted+escaped; bool/char/Guid/dates/times/TimeSpan
invariant; enums by numeric value (:180–182); numeric primitives invariant; dictionaries must be
string-keyed and are written with keys ordinal-sorted (:207, :223–257); collections nest (max
depth 32, :33); unsupported types (including anonymous types) throw `ArgumentException` (:213–219).

## `DejaOptions` / `QueryDefaults` — src/Deja/DejaOptions.cs:7, :59

| Property | Default | Line |
|---|---|---|
| `DefaultStaleTime` | `TimeSpan.Zero` | DejaOptions.cs:16 |
| `DefaultCacheTime` | 5 minutes | DejaOptions.cs:22 |
| `DefaultRefetchOnMount` | `RefetchOnMount.IfStale` | DejaOptions.cs:28 |
| `MaxEntries` | `null` (no cap) | DejaOptions.cs:34 |
| `EvictionInterval` | 1 minute | DejaOptions.cs:37 |
| `StructuralComparison` | `false` | DejaOptions.cs:45 |
| `TimeProvider` | `TimeProvider.System` | DejaOptions.cs:51 |

`QueryDefaults`: `StaleTime` (:62), `CacheTime` (:65), `RefetchOnMount` (:68) — null falls
through to shorter prefix, then options.

## `DejaComponentBase` — src/Deja/DejaComponentBase.cs:30

| Member | Line | Notes |
|---|---|---|
| `protected CancellationToken ComponentToken` | DejaComponentBase.cs:69 | cancelled on dispose; post-dispose reads return an already-cancelled token, never throw |
| ctor | DejaComponentBase.cs:97 | throws on redeclared `Dispose`/`DisposeAsync` |
| `protected override void OnInitialized()` | DejaComponentBase.cs:108 | attaches declared state; overrides must call base |
| `public override Task SetParametersAsync(ParameterView)` | DejaComponentBase.cs:116 | reports skipped base.OnInitialized() |
| `IAsyncDisposable.DisposeAsync()` | DejaComponentBase.cs:151 | explicit: cancel token → Dispose hook → DisposeAsync hook → detach + dispose owned state |
| `protected virtual void Dispose()` | DejaComponentBase.cs:227 | synchronous cleanup hook |
| `protected virtual ValueTask DisposeAsync()` | DejaComponentBase.cs:238 | asynchronous cleanup hook |
| `protected TState Observe<TState>(TState) where TState : IDejaObservable` | DejaComponentBase.cs:347 | returns state for inline use; double-attach is a no-op; throws when another component owns it |

Guard rails:

- **Disposal redeclaration throw** (DejaComponentBase.cs:250–317): `@implements IAsyncDisposable`
  → `InvalidOperationException` (redeclared DisposeAsync replaces Deja's disposal);
  `@implements IDisposable` → `InvalidOperationException` (redeclared Dispose is never called).
  Explicit interface re-implementations are also caught; `Dispose(bool)`-style helpers are allowed.
- **Skipped `base.OnInitialized()` console error** (DejaComponentBase.cs:321–331): reported once,
  written to stderr → surfaces as `console.error` on WASM.

Discovery (DejaComponentBase.cs:405–457): fields and properties scanned once at initialization,
walking the type hierarchy; `[Parameter]`, `[CascadingParameter]`, `[Inject]` members and
compiler-generated backing fields are skipped. Later-created state needs `Observe()`.

## `DejaObservable` / `IDejaObservable` — src/Deja/DejaObservable.cs:21, :86

- `IDisposable Attach(Action)` (:26): single listener slot; occupied slot →
  `InvalidOperationException` (:31–36). Detaching is idempotent (:53–61).
- `protected void NotifyChanged()` (:51): no-op when nothing attached.
- Single-listener rule is what keeps sibling components from re-rendering each other
  (remarks, :81–85).

## `DisplayUserException` — src/Deja/Errors.cs:8

`string DisplayMessage` (:11) plus five constructors (:14–52): empty; message doubles as display
message; message + inner; display message + internal message; display + internal + inner.

## Internal but documented behaviour

- `TimeoutRetry` (Errors.cs:59): retries exactly once when a `TaskCanceledException` carries an
  inner `TimeoutException` (HttpClient.Timeout expiry) and the caller's token is not cancelled.
  Shared by the uncached path and cache-entry fetches.
- `CacheEntryState` (CacheEntry.cs:505) — public record returned by `GetState`: `HasData`,
  `UpdatedAt`, `ErrorMessage`, `ErrorUpdatedAt`, `IsInvalidated`, `IsFetching`, `SubscriberCount`.
- Cache entry lifecycle: born eviction-eligible until first subscriber (CacheEntry.cs:35–38);
  subscriber arriving clears `EvictAt` (CacheEntry.cs:86–97); last subscriber leaving starts the
  eviction clock and cancels the in-flight fetch (CacheEntry.cs:374–393).
