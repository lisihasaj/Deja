# Deja Documentation Site — Implementation Plan

A step-by-step plan for building a documentation website for the Deja package: a polished
landing page plus organised subpages, with a sidebar in the style of a regular documentation
site, and live demos powered by a public JSON API.

---

## Decisions locked in

| Choice | Value |
|---|---|
| Stack | Blazor WebAssembly (`net10.0`), dogfooding Deja itself |
| Demo API | JSONPlaceholder (`https://jsonplaceholder.typicode.com`) |
| Location | `docs/Deja.Docs/` at repo root |
| API reference | Hand-written, curated from source XML docs |
| Theme | `#5C00B3` primary, `#FDFDFB` surface |

**Source of truth is `src/Deja/*.cs`, not `README.md`.** Every documented signature, default
value, and behavioural claim must be traced to a specific line in the implementation. Where the
README and the code disagree, the code wins and the discrepancy is reported rather than silently
resolved.

### Why Blazor WASM rather than a static site or Docusaurus

Every code sample can be a *real running demo*: the cache page genuinely shares cache entries,
refetch buttons genuinely refetch against the live API, and the state inspector shows the actual
`Query<T>` state machine advancing. A static site or a Markdown generator can only show dead code
listings, and any "live" fetching would be JavaScript pretending to be Deja — which
misrepresents the package. The cost is maintaining a Blazor app; the payoff is that the docs
double as proof the library works.

### Why JSONPlaceholder

No API key, CORS-enabled, stable, and — critically — it *fakes* writes: `POST`/`PUT`/`DELETE`
return a realistic `201`/`200` response but persist nothing. Mutation demos are therefore safe to
hammer and always reset clean between visitors.

---

## Phase 0 — API inventory

**Do this before any page is written.**

Produce `docs/Deja.Docs/api-inventory.md` as a working artifact: every public type and member,
extracted from source, with the `file:line` it came from. This is the checklist the API pages are
written against, so nothing gets missed and nothing gets invented.

### Verified surface

Extracted from the source in `src/Deja/`.

#### `Query<T>` — `src/Deja/Query.cs`

Bindable state:

- `IsLoading`, `IsError`, `ErrorMessage`, `Data`, `ReFetchCount`, `IsReFetching`
- `UpdatedAt`, `IsCachedData`, `IsStale`

Methods and constructors:

- `Execute(QueryKey?, Func<CancellationToken, Task<T>>, Action<QueryParameters<T>>?)` — keyed shorthand
- `Execute(Func<CancellationToken, Task<T>>, Action<QueryParameters<T>>?)` — unkeyed shorthand
- `Execute(QueryParameters<T>)` — full form
- `Refetch(RefetchParameters<T>?)`
- `ClearData()` / `ClearData(bool cancelCurrentRequest)`
- `Dispose()`
- `Query()`, `Query(DejaClient)`

#### `QueryParameters<T>`

18 properties: `QueryKey`, `QueryFunction`, `CancellationToken`, eight callbacks (sync/async ×
success/error/displayUserError/settled), `Client`, `StaleTime`, `CacheTime`, `RefetchOnMount`,
`Enabled`, `Select`, `PlaceholderData`.

#### `RefetchParameters<T>`

The one-shot override subset: callbacks, `CancellationToken`, `StaleTime`, `CacheTime`.
Deliberately *not* the key or the fetch function — changing those is a new query, not a refetch.

#### `Mutation<T>` — `src/Deja/Mutation.cs`

- State: `IsLoading`, `IsError`, `ErrorMessage`, `Data`
- Three `Execute` overloads
- `MutationParameters<T>` with four mutually-exclusive function shapes: `MutationFunction`,
  `VoidMutationFunction`, `CancellableMutationFunction`, `CancellableVoidMutationFunction`
- `InvalidateKeys`

#### `DejaClient` — `src/Deja/DejaClient.cs`

`GetData<T>`, `TryGetData<T>`, `GetState`, `SetData<T>` (value and updater forms),
`InvalidateAsync`, `InvalidateAllAsync`, `RefetchAsync`, `Remove`, `Clear`, `SetDefaults`,
`Dispose`.

Supporting types: `QueryFilter`, `InvalidateOptions`, `RefetchType`, `AddDeja()`.

#### `QueryKey` — `src/Deja/QueryKey.cs`

`Of(params object?[])`, `FromString`, implicit `string` conversion, `StartsWith`, value equality,
and the `IQueryKeySegment` extension interface.

#### `DejaOptions` — `src/Deja/DejaOptions.cs`

| Property | Default |
|---|---|
| `DefaultStaleTime` | `TimeSpan.Zero` |
| `DefaultCacheTime` | 5 minutes |
| `DefaultRefetchOnMount` | `RefetchOnMount.IfStale` |
| `MaxEntries` | `null` (no cap) |
| `EvictionInterval` | 1 minute |
| `StructuralComparison` | `false` |
| `TimeProvider` | `TimeProvider.System` |

Plus `QueryDefaults` for per-prefix defaults.

#### `DejaComponentBase` — `src/Deja/DejaComponentBase.cs`

`ComponentToken`, `Observe<TState>`, the `Dispose()` / `DisposeAsync()` hooks, and two guard rails
that each deserve their own docs section:

- the **disposal-redeclaration throw** (a derived component declaring its own `Dispose` /
  `DisposeAsync` instead of overriding the hooks)
- the **skipped `base.OnInitialized()` console error**

#### `DejaObservable` / `IDejaObservable` — `src/Deja/DejaObservable.cs`

`Attach(Action)`, `NotifyChanged()`, and the single-listener rule that underpins the isolation
guarantee.

---

## Phase 1 — Project scaffolding

```
docs/
  Deja.Docs/
    Deja.Docs.csproj              net10.0, BlazorWebAssembly, ProjectReference -> src/Deja
    Program.cs                    HttpClient(jsonplaceholder) + AddDeja(...)
    App.razor
    _Imports.razor
    wwwroot/
      index.html
      favicon
```

Add `docs/Deja.Docs/Deja.Docs.csproj` to `Deja.slnx`.

`Program.cs` registers a named `HttpClient` for JSONPlaceholder, the typed API clients, and
`AddDeja` with **explicitly non-default** options, so the docs demonstrate configuration rather
than silently inheriting defaults:

```csharp
builder.Services.AddDeja(options =>
{
    options.DefaultStaleTime = TimeSpan.FromSeconds(10);
    options.DefaultCacheTime = TimeSpan.FromMinutes(5);
});
```

---

## Phase 2 — Design system

Lives in `wwwroot/css/`, split into `tokens.css`, `layout.css`, `components.css`, `code.css` — so
future pages inherit the system rather than growing one-off CSS.

### Palette

Derived from the two theme colours.

| Token | Light | Role |
|---|---|---|
| `--deja-primary` | `#5C00B3` | brand, links, active nav |
| `--deja-primary-hover` | `#4A0091` | hover / pressed |
| `--deja-primary-soft` | `#F3E9FC` | active nav pill, callout tint |
| `--deja-surface` | `#FDFDFB` | page background |
| `--deja-elevated` | `#FFFFFF` | cards, code blocks |
| `--deja-ink` | `#1A1522` | body text |
| `--deja-muted` | `#6B6478` | secondary text |
| `--deja-border` | `#E8E4EE` | hairlines |

Plus semantic tokens (`--deja-success`, `--deja-warning`, `--deja-danger`) for demo state badges.

A dark-mode block via `prefers-color-scheme` redefines only the tokens. Worth including from the
start — retrofitting it later is significantly costlier.

### Contrast check

`#5C00B3` on `#FDFDFB` is roughly 11:1 — comfortably AAA. White on `#5C00B3` is about 9.9:1. Both
are safe.

---

## Phase 3 — Shell and navigation

`Layout/DocsLayout.razor` — three columns: sidebar / content / on-page "On this page" table of
contents.

**The sidebar is data-driven, not hardcoded markup.** `Navigation/DocsNav.cs` holds an
`IReadOnlyList<NavSection>` of `(Title, Href, Badge?)`. Adding a future page is one list entry,
and the sidebar, the prev/next footer links, and the search index all read from it.

```
Getting started        Guides                   API reference
  Introduction           Queries                  Query<T>
  Installation           Mutations                QueryParameters<T>
  Quick start            Component base           RefetchParameters<T>
                         The cache                Mutation<T>
Live demos               Query keys               MutationParameters<T>
  Todo list              Cancellation             DejaClient
  Shared cache           Refetching               QueryKey
  Isolation              Stale & cache time       DejaOptions
  Optimistic write       Error handling           DejaComponentBase
                                                  Enums & options
```

Components to build: `Sidebar.razor` (collapsible sections, active-route highlight, mobile
drawer), `TableOfContents.razor`, `PrevNextLinks.razor`, `ThemeToggle.razor`.

---

## Phase 4 — Reusable doc primitives

Built once, used everywhere. This is what makes future pages cheap.

- **`CodeBlock.razor`** — syntax highlighting, copy button, optional filename tab, optional line
  highlighting
- **`Callout.razor`** — `Info` / `Warning` / `Danger` / `Tip` variants
- **`ApiTable.razor`** — signature, type, default, description; renders `QueryParameters<T>` and
  friends consistently
- **`ApiMember.razor`** — anchored heading, signature block, and prose for one member
- **`DemoCard.razor`** — the key one: live demo pane on top, tabbed source underneath, so every
  demo shows its own real code
- **`StateInspector.razor`** — renders a query's live `IsLoading` / `IsError` / `IsCachedData` /
  `IsStale` / `ReFetchCount` / `UpdatedAt` as badges. The single most persuasive element on the
  site: readers *watch* the state machine move.
- **`CacheInspector.razor`** — polls `DejaClient.GetState` for the demo keys and shows entry
  status, subscriber count, and staleness

### Syntax highlighting

Prism.js vendored into `wwwroot/lib/` rather than loaded from a CDN — keeps the site
offline-capable and avoids a third-party runtime dependency in the docs for a dependency-free
package. C# and Razor grammars only.

---

## Phase 5 — Demo data layer

`Services/JsonPlaceholder/`:

```csharp
public sealed record TodoDto(int Id, int UserId, string Title, bool Completed);
public sealed record PostDto(int Id, int UserId, string Title, string Body);
public sealed record UserDto(int Id, string Name, string Username, string Email);
```

`JsonPlaceholderApi` — every method accepts a `CancellationToken`, so the cancellation docs are
demonstrable rather than merely described:

- `GetTodosAsync(int limit, CancellationToken)`
- `GetTodoAsync(int id, CancellationToken)`
- `GetPostsAsync(int? userId, int page, CancellationToken)` — feeds the hierarchical-key demo
- `GetUserAsync(int id, CancellationToken)`
- `CreateTodoAsync(NewTodo, CancellationToken)` → `POST`, faked `201`
- `UpdateTodoAsync(TodoDto, CancellationToken)` → `PUT`
- `DeleteTodoAsync(int id, CancellationToken)` → `DELETE`

### Two wrappers that make the demos legible

- **`LatencySimulator`** — an optional artificial delay slider (0–2000 ms) in the site chrome.
  JSONPlaceholder is fast enough that loading states flash by invisibly; without this, the
  `IsLoading` / `IsReFetching` distinction cannot be demonstrated.
- **`FailureSwitch`** — a "make the next request fail" toggle, so the error-handling and
  `DisplayUserException` docs have a working demo rather than a described one.

Both live in a floating **Demo Controls** panel available on every demo page.

### Key namespace for demos

A single `DocsKeys` static class, so the docs model good key hygiene:

```csharp
public static class DocsKeys
{
    public static QueryKey Todos => QueryKey.Of("todos");
    public static QueryKey TodoList(int limit) => QueryKey.Of("todos", "list", limit);
    public static QueryKey TodoDetail(int id) => QueryKey.Of("todos", "detail", id);
    public static QueryKey Posts(int? userId, int page) =>
        QueryKey.Of("posts", "list", new Dictionary<string, object?> { ["userId"] = userId, ["page"] = page });
}
```

This also gives the `QueryKey` page a real prefix-invalidation story: invalidating `["todos"]`
hits both the list and the detail entries.

---

## Phase 6 — Content pages

### Getting started (3 pages)

1. **Introduction** — the problem stated as boilerplate: `bool _loading`, `try`/`catch`,
   `StateHasChanged`, `IDisposable`. Shown as a before/after diff against Deja.
2. **Installation**
3. **Quick start**

### Guides (8 pages)

Each ends with a live `DemoCard`.

1. **Queries** — the three `Execute` overloads, the full state surface, `Enabled`, `Select`,
   `PlaceholderData`
2. **Mutations** — the four function shapes, `InvalidateKeys`, and the gotcha documented in the
   source: `Execute` throws an `InvalidOperationException` wrapping the original **only when no
   error callback was supplied**
3. **Component base** — discovery at `OnInitialized`, `Observe()` for later-created state, the
   single-owner rule, and both guard rails (disposal redeclaration throw; skipped
   `base.OnInitialized()`)
4. **The cache** — `AddDeja`, instant render, one-fetch-per-key, and the Scoped/Blazor Server
   warning
5. **Query keys** — canonical form, dictionary normalisation, `StartsWith` prefix matching,
   `IQueryKeySegment`, and the "anonymous types are not supported" rule
6. **Cancellation** — `ComponentToken`, a per-call token *replaces* rather than combines with the
   ambient one, and the subtle case: on the cached path a caller's token detaches that caller
   **without** cancelling the shared fetch
7. **Refetching and staleness** — `Refetch` bypasses `Enabled` / `RefetchOnMount` / staleness,
   one-shot overrides, the `RefetchOnMount` enum, and the precedence chain
8. **Error handling** — `DisplayUserException`, callback ordering, and the `TimeoutRetry`
   single-retry policy

### Live demos (4 pages)

1. **Todo list** — full CRUD
2. **Shared cache** — two siblings, one key, one fetch, with a live subscriber counter
3. **Isolation** — two unkeyed siblings that provably cannot re-render each other
4. **Optimistic write** — `SetData` updater form plus rollback

### API reference (10 pages)

One page per type, written against the Phase 0 inventory. Each member gets an anchored signature,
a parameters table, exceptions, remarks, and cross-links.

---

## Phase 7 — Search, polish, CI

- **Search** — client-side, over a build-time-generated `search-index.json` (title, headings, and
  keywords per page). No server, no external service.
- **Polish** — deep-linkable anchors on every API member, a 404 page, skip-to-content, and
  reduced-motion support.
- **CI** — `.github/workflows/docs.yml` runs `dotnet publish` on the docs project and deploys
  `wwwroot` to GitHub Pages, rewriting `<base href>` for the repo subpath and copying
  `index.html` to `404.html` for SPA routing.
- **`.nojekyll`** — required, or GitHub Pages ignores Blazor's `_framework` folder.

---

## Extension points designed in from the start

| Future need | Already accommodated |
|---|---|
| New doc page | One entry in `DocsNav.cs` |
| New API type page | Copy an `ApiMember` page, add a nav entry |
| Versioned docs (v0.x / v1.x) | Nav model carries a version discriminator; route prefix reserved |
| Roadmap features (polling, optimistic updates, persistence, focus refetch) | Guides folder has a slot per feature; nav sections are open lists |
| Package split (`Deja.Components`) | API reference is already grouped by assembly |
| Interactive playground | `DemoCard` already separates the demo pane from the source pane |

---

## Open items and recommendations

### 1. Existing `samples/Deja.Sample` overlaps the new demos

Recommendation: keep it. It is the bare scratch app; the docs site is the polished one. But the
four demo pages will duplicate its `/cache` and `/isolation` pages. The alternative is to port
those two into the docs site and slim the sample down.

### 2. Target framework

The library multi-targets `net8.0;net9.0;net10.0` but the existing sample is `net10.0` only.
Recommendation: match at `net10.0`, unless the docs site should prove `net8.0` compatibility.

### 3. CORS and offline resilience

JSONPlaceholder is CORS-open and reliable, but a docs site that breaks when it is down is fragile.
Recommendation: add a fallback to embedded seed data with a visible "offline sample data" badge —
the demos keep working, and the fallback itself demonstrates `PlaceholderData`.

### 4. README and source drift

Any place where the README describes behaviour the code does not implement gets collected into a
list and reported, rather than either version being silently documented.

---

## Execution order

| Phase | Deliverable | Depends on |
|---|---|---|
| 0 | `api-inventory.md` | — |
| 1 | Scaffolded project, builds and runs | — |
| 2 | Design tokens and base stylesheets | 1 |
| 3 | Layout shell, data-driven sidebar, routing | 1, 2 |
| 4 | Reusable doc primitives | 2, 3 |
| 5 | JSONPlaceholder services, demo controls | 1 |
| 6 | All content pages | 0, 4, 5 |
| 7 | Search, polish, CI deployment | 6 |
