# Deja.Docs — SEO & Social Metadata Plan

A complete audit of the documentation site and a page-by-page specification of the metadata
required for discoverability on Google, Bing, Facebook, X/Twitter, LinkedIn, Slack and Discord.

- **Site**: `docs/Deja.Docs` — Blazor WebAssembly SPA
- **Host**: GitHub Pages, served from the repo subpath `/Deja/`
- **Canonical origin**: `https://lisihasaj.github.io/Deja/`
- **Pages**: 26 routable pages (1 landing, 3 getting started, 4 demos, 9 guides, 10 API, 1 not-found)
- **Languages**: English and German, switched client-side via `localStorage`

---

## 1. Current state

### What already exists

| Item | Status |
|---|---|
| `<title>` per page (`<PageTitle>`) | ✅ Present on all 26 pages, bilingual |
| Static fallback `<title>` in `index.html` | ✅ Present |
| Static `<meta name="description">` in `index.html` | ⚠️ One global description, identical for all 26 pages |
| `HeadOutlet` registered in `Program.cs` | ✅ Present — `<HeadContent>` works today |
| `lang` attribute on `<html>` | ✅ Set, and updated client-side on language switch |
| `favicon.svg` | ✅ Present |
| Semantic HTML (`<main>`, `<article>`, one `<h1>` per page) | ✅ Good |
| Skip link, focus management | ✅ Good |

### What is missing

| Gap | Impact |
|---|---|
| **Per-page `<meta name="description">`** | Google writes its own snippets from page text; CTR suffers and snippets are unpredictable |
| **Open Graph tags** (`og:*`) | Links shared to Facebook, LinkedIn, Slack, Discord, WhatsApp render as a bare URL with no title, description or image |
| **Twitter Card tags** (`twitter:*`) | Links on X render without a card |
| **A social share image** | No preview thumbnail anywhere; the single highest-impact item for click-through on social |
| **Canonical URLs** | Duplicate-content risk from `?query` params, trailing-slash variants and the `404.html` fallback |
| **`robots.txt`** | No crawler guidance; no sitemap pointer |
| **`sitemap.xml`** | 25 pages are reachable only via client-side routing — crawlers have no reliable list |
| **JSON-LD structured data** | No eligibility for rich results (breadcrumbs, software listing, FAQ, site-name) |
| **Prerendered HTML for crawlers** | See §2 — the single biggest structural risk |
| **Distinct URLs per language** | German content is invisible to search engines — see §3 |
| **Real 404 status** | `404.html` serves the SPA shell, producing soft-404s |
| **`theme-color`, PWA manifest, apple touch icon** | Minor polish; affects mobile browser chrome and "add to home screen" |

---

## 2. Structural issue: client-side rendering

**This is the highest-priority item. Metadata alone will not fix it.**

Blazor WebAssembly ships an empty `<div id="app">` and fills it from a `.wasm` runtime. That means:

- **Googlebot** *can* render JavaScript, but does so on a deferred second pass. Blazor WASM's runtime
  download (several MB) plus the render pass frequently exceeds the crawler's patience budget.
  Indexing is unreliable and slow.
- **Bing, DuckDuckGo, Yandex** render JS inconsistently or not at all.
- **Facebook, X/Twitter, LinkedIn, Slack, Discord, WhatsApp crawlers do not execute JavaScript at all.**
  They read the raw HTML response and stop. Any `og:` tag emitted by `<HeadContent>` at runtime is
  invisible to every one of them.

**Consequence:** dynamic `<HeadContent>` metadata is necessary but *not sufficient*. Social platforms
will only ever see what is in the served HTML file.

### Recommended fix — static prerender at build time (Option A, preferred)

Extend `.github/workflows/docs.yml` to emit one real `.html` file per route, each carrying its own
complete `<head>`. The existing steps already rewrite `<base href>` and copy `index.html` to
`404.html`, so this is an extension of a pattern already in place.

Two viable implementations:

1. **Generation script (simplest, no new runtime dependency).** After `dotnet publish`, run a small
   script that reads a metadata manifest (see §6) and, for each route, writes
   `publish/wwwroot/<route>/index.html` — a copy of `index.html` with the per-page `<title>`,
   `<meta name="description">`, `og:*`, `twitter:*`, `<link rel="canonical">` and JSON-LD injected.
   The Blazor router still takes over on load and renders the correct page for the URL, so the user
   experience is unchanged. Crawlers get fully-formed HTML with zero JS execution.

2. **Headless-browser prerender.** Serve the published site locally in CI, crawl each route with
   Playwright, and save the rendered DOM. Higher fidelity (body text is prerendered too, which also
   helps Google index the actual content), but slower and more brittle.

**Recommendation: implement (1) first** — it delivers 100% of the social-card and metadata benefit at
a fraction of the complexity. Consider (2) later if Google Search Console shows body content is not
being indexed.

`DocsNav` (`Navigation/DocsNav.cs`) is already the single source of truth for site structure and is
the natural place to hang the metadata used by the generator, the sitemap and the runtime tags alike.

### Option B — runtime-only `<HeadContent>` (fallback)

If prerendering is out of scope, still add `<HeadContent>` to every page. Google will eventually pick
it up. Accept that social previews will be identical site-wide, and set a good *global* set of `og:`
tags in `index.html` so at least every shared link shows the Deja card rather than nothing.

---

## 3. Structural issue: bilingual content on one URL

English and German content currently live at the **same URL**, toggled by a `localStorage` flag read
by `LanguageService`. Search engines see only the English default. All German content — roughly half
the site's written material — is unindexable.

### Recommended fix

Move the language into the URL and serve distinct, crawlable pages:

```
https://lisihasaj.github.io/Deja/guides/queries      → English (default, x-default)
https://lisihasaj.github.io/Deja/de/guides/queries   → German
```

Then on every page emit reciprocal `hreflang` annotations:

```html
<link rel="alternate" hreflang="en" href="https://lisihasaj.github.io/Deja/guides/queries" />
<link rel="alternate" hreflang="de" href="https://lisihasaj.github.io/Deja/de/guides/queries" />
<link rel="alternate" hreflang="x-default" href="https://lisihasaj.github.io/Deja/guides/queries" />
```

Rules: every `hreflang` set must be reciprocal (each URL lists itself and all its alternates), and
`<html lang>` must match the served language. `localStorage` may still remember the preference, but
it should *redirect* to the language-prefixed URL rather than swap content in place.

**Effort/impact:** this is a meaningful refactor (routes, `LanguageService`, sitemap, generator). If
deferred, at minimum keep `<html lang="en">` accurate and do **not** claim German support in metadata.
Scope it as a phase-two item; the plan below assumes English URLs are canonical and marks the German
additions clearly so they can be layered in later.

---

## 4. Global metadata — `wwwroot/index.html`

These serve as defaults and as the fallback for any route the prerender step misses.

```html
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />

<title>Deja — data fetching and caching for Blazor</title>
<meta name="description" content="Deja brings data fetching, mutation and caching primitives to Blazor WebAssembly and Server: bindable loading and error state, a shared keyed cache, request deduplication and automatic cancellation. Dependency-free." />

<link rel="canonical" href="https://lisihasaj.github.io/Deja/" />

<meta name="robots" content="index, follow, max-snippet:-1, max-image-preview:large, max-video-preview:-1" />
<meta name="author" content="Lisjan Hasaj" />
<meta name="theme-color" content="#0d1117" media="(prefers-color-scheme: dark)" />
<meta name="theme-color" content="#ffffff" media="(prefers-color-scheme: light)" />

<!-- Open Graph — Facebook, LinkedIn, Slack, Discord, WhatsApp -->
<meta property="og:type" content="website" />
<meta property="og:site_name" content="Deja" />
<meta property="og:locale" content="en_US" />
<meta property="og:title" content="Deja — data fetching and caching for Blazor" />
<meta property="og:description" content="Declare a query, bind its loading and error state, and let a shared keyed cache dedupe the requests. Blazor WebAssembly and Server, dependency-free." />
<meta property="og:url" content="https://lisihasaj.github.io/Deja/" />
<meta property="og:image" content="https://lisihasaj.github.io/Deja/social/og-default.png" />
<meta property="og:image:width" content="1200" />
<meta property="og:image:height" content="630" />
<meta property="og:image:alt" content="Deja — data fetching and caching for Blazor" />

<!-- X / Twitter -->
<meta name="twitter:card" content="summary_large_image" />
<meta name="twitter:title" content="Deja — data fetching and caching for Blazor" />
<meta name="twitter:description" content="Declare a query, bind its loading and error state, and let a shared keyed cache dedupe the requests. Blazor WebAssembly and Server, dependency-free." />
<meta name="twitter:image" content="https://lisihasaj.github.io/Deja/social/og-default.png" />
<meta name="twitter:image:alt" content="Deja — data fetching and caching for Blazor" />

<link rel="icon" type="image/svg+xml" href="favicon.svg" />
<link rel="apple-touch-icon" href="apple-touch-icon.png" />
<link rel="manifest" href="site.webmanifest" />
```

> **Note on `og:url` and `canonical`:** these must be **absolute** URLs including the `/Deja/`
> subpath. The workflow's `sed` step already rewrites `<base href>` for the subpath — extend it (or
> the generator) so absolute URLs are correct in production and are not left pointing at `localhost`.

> **`twitter:site` / `twitter:creator`:** add `<meta name="twitter:creator" content="@handle" />` if
> there is an X account to attribute. Omit rather than guess — a wrong handle credits a stranger.

---

## 5. Assets to create

| Asset | Spec | Notes |
|---|---|---|
| `wwwroot/social/og-default.png` | 1200×630 PNG, < 1 MB | Site-wide fallback card. Deja wordmark + tagline "Data fetching and caching for Blazor" on the brand background. Keep text in the middle 1000×524 — LinkedIn and Slack crop edges. |
| `wwwroot/social/og-getting-started.png` | 1200×630 | Optional, per-section card |
| `wwwroot/social/og-guides.png` | 1200×630 | Optional |
| `wwwroot/social/og-api.png` | 1200×630 | Optional |
| `wwwroot/social/og-demos.png` | 1200×630 | Optional |
| `wwwroot/apple-touch-icon.png` | 180×180 PNG | iOS home screen |
| `wwwroot/site.webmanifest` | JSON | `name`, `short_name`, `theme_color`, `background_color`, icons |
| `wwwroot/robots.txt` | See §7 | |
| `wwwroot/sitemap.xml` | See §7 | Generate from `DocsNav` |

**Minimum viable:** `og-default.png` alone covers every page. Section cards are a refinement, and
per-page generated cards (title rendered into the image at build time) are a further step worth
taking only once the basics ship.

All social images **must** be referenced by absolute URL. Relative paths are silently dropped by
every major social crawler.

---

## 6. Per-page metadata specification

Field conventions used below:

- **Title** — target 50–60 characters. Ends with `— Deja` (or `— Deja API`) for brand recall. Already
  correct on every page; retained here for completeness and to confirm no changes are needed.
- **Description** — target **140–160 characters**. Must be unique, lead with the concrete benefit,
  and read as prose rather than a keyword list. Drawn from each page's existing `lead` paragraph,
  which is already well written — these are lightly tightened for length.
- **Keywords** — *not* a meta tag (Google ignores `<meta name="keywords">` entirely). Listed here as
  the terms the page should rank for, to be verified as present in the page's visible body copy,
  headings, and the `DocsNav` search keywords.
- **OG image** — falls back to `og-default.png` where no section card is specified.

---

### 6.1 Landing page

**`/`** — `Pages/Home.razor`

| Field | Value |
|---|---|
| Title | `Deja — data fetching and caching for Blazor` |
| Description | `Data fetching, caching and synchronization for Blazor without the boilerplate. Declare a query, bind loading and error state, and let a shared keyed cache dedupe requests.` |
| Canonical | `https://lisihasaj.github.io/Deja/` |
| `og:type` | `website` |
| OG image | `og-default.png` |
| Target keywords | blazor data fetching, blazor cache, blazor query library, react query for blazor, tanstack query blazor alternative, blazor state management, blazor webassembly caching |
| JSON-LD | `SoftwareSourceCode` + `WebSite` (see §8) |

> **Positioning note.** "React Query for Blazor" / "TanStack Query for Blazor" is the single highest-
> value search intent for this library — it is how .NET developers coming from React will phrase the
> search. The landing page and Introduction should each contain one natural sentence naming that
> comparison in the body copy. Do not keyword-stuff it; one honest mention per page is what ranks.

---

### 6.2 Getting started

**`/getting-started/introduction`** — `Pages/GettingStarted/Introduction.razor`

| Field | Value |
|---|---|
| Title | `Introduction — Deja` |
| Description | `Deja removes the boilerplate around calling an API in Blazor, caches what you fetched, and keeps that data synchronized across every component showing it.` |
| Canonical | `.../Deja/getting-started/introduction` |
| `og:type` | `article` |
| OG image | `og-getting-started.png` |
| Keywords | why deja, blazor api boilerplate, blazor loading state, blazor data synchronization, déjà vu cache |

**`/getting-started/installation`** — `Pages/GettingStarted/Installation.razor`

| Field | Value |
|---|---|
| Title | `Installation — Deja` |
| Description | `Install the Deja NuGet package and register the shared query cache with AddDeja(). Multi-targets net8.0, net9.0 and net10.0 on Blazor WebAssembly and Server.` |
| Canonical | `.../Deja/getting-started/installation` |
| `og:type` | `article` |
| OG image | `og-getting-started.png` |
| Keywords | install deja, dotnet add package deja, AddDeja, blazor server setup, blazor webassembly setup, net8 net9 net10 |
| JSON-LD | Add `HowTo` — this page is a clean multi-step install |

**`/getting-started/quick-start`** — `Pages/GettingStarted/QuickStart.razor`

| Field | Value |
|---|---|
| Title | `Quick start — Deja` |
| Description | `From zero to a cached, cancellable, auto-rendering Blazor query in three steps: declare and execute it, bind its state, then write with a mutation and invalidate the key.` |
| Canonical | `.../Deja/getting-started/quick-start` |
| `og:type` | `article` |
| OG image | `og-getting-started.png` |
| Keywords | blazor query tutorial, blazor first query, Execute, IsLoading, blazor mutation example, blazor data fetching example |
| JSON-LD | Add `HowTo` with the three steps as `HowToStep` |

---

### 6.3 Live demos

Demo pages are the most shareable content on the site — they are the links that get posted to
Reddit, X and Discord. They deserve the strongest social cards and, ideally, per-page OG images
showing the running demo.

**`/demos/todo-list`** — `Pages/Demos/TodoList.razor`

| Field | Value |
|---|---|
| Title | `Todo list demo — Deja` |
| Description | `Full CRUD against a live API in Blazor: a keyed query for the list, three mutations for create, update and delete, and SetData applying results to the shared cache.` |
| Canonical | `.../Deja/demos/todo-list` |
| `og:type` | `article` |
| OG image | `og-demos.png` (ideally a screenshot of the demo) |
| Keywords | blazor crud example, blazor todo list, blazor mutation demo, SetData, live blazor demo |

**`/demos/shared-cache`** — `Pages/Demos/SharedCache.razor`

| Field | Value |
|---|---|
| Title | `Shared cache demo — Deja` |
| Description | `Two sibling Blazor components execute the same query key. One fetch serves both, a refetch from either updates both, and the inspector shows the single shared cache entry.` |
| Canonical | `.../Deja/demos/shared-cache` |
| `og:type` | `article` |
| OG image | `og-demos.png` |
| Keywords | blazor request deduplication, shared cache blazor, one fetch two components, query key subscribers |

**`/demos/isolation`** — `Pages/Demos/Isolation.razor`

| Field | Value |
|---|---|
| Title | `Isolation demo — Deja` |
| Description | `Two unkeyed sibling Blazor components that provably cannot re-render each other: Deja state notifies exactly one listener — the component that owns it.` |
| Canonical | `.../Deja/demos/isolation` |
| `og:type` | `article` |
| OG image | `og-demos.png` |
| Keywords | blazor re-render isolation, blazor unnecessary re-renders, single listener, component isolation |

**`/demos/optimistic-write`** — `Pages/Demos/OptimisticWrite.razor`

| Field | Value |
|---|---|
| Title | `Optimistic write demo — Deja` |
| Description | `Flip the Blazor UI immediately with SetData's updater form, run the request after, and roll back to a snapshot if it fails. Optimistic updates with rollback.` |
| Canonical | `.../Deja/demos/optimistic-write` |
| `og:type` | `article` |
| OG image | `og-demos.png` |
| Keywords | blazor optimistic update, optimistic ui blazor, SetData updater, rollback snapshot |

---

### 6.4 Guides

**`/guides/queries`** — `Pages/Guides/Queries.razor`

| Field | Value |
|---|---|
| Title | `Queries — Deja` |
| Description | `A Query<T> tracks a single asynchronous read in Blazor and exposes its lifecycle as bindable state. The three ways to run one and the parameters that shape an execution.` |
| Canonical | `.../Deja/guides/queries` |
| OG image | `og-guides.png` |
| Keywords | blazor async read, Query<T>, Execute, Enabled, Select, PlaceholderData, bindable loading state |

**`/guides/mutations`** — `Pages/Guides/Mutations.razor`

| Field | Value |
|---|---|
| Title | `Mutations — Deja` |
| Description | `A Mutation<T> gives Blazor writes the same bindable treatment as reads — IsLoading, IsError, ErrorMessage, Data — plus automatic cache invalidation on success.` |
| Canonical | `.../Deja/guides/mutations` |
| OG image | `og-guides.png` |
| Keywords | blazor mutation, InvalidateKeys, blazor post request, void mutation, cache invalidation |

**`/guides/component-base`** — `Pages/Guides/ComponentBase.razor`

| Field | Value |
|---|---|
| Title | `Component base — Deja` |
| Description | `DejaComponentBase discovers the queries and mutations a Blazor component owns, re-renders it when they change, scopes them to its lifetime, and cleans up on dispose.` |
| Canonical | `.../Deja/guides/component-base` |
| OG image | `og-guides.png` |
| Keywords | DejaComponentBase, blazor automatic re-render, Observe, StateHasChanged, blazor dispose |

**`/guides/rendering`** — `Pages/Guides/Rendering.razor`

| Field | Value |
|---|---|
| Title | `Rendering & re-renders — Deja` |
| Description | `A Blazor component should re-render when what it displays changes, and not otherwise. How Deja enforces that with one notification per transition and coalesced renders.` |
| Canonical | `.../Deja/guides/rendering` |
| OG image | `og-guides.png` |
| Keywords | blazor re-render performance, render count, StateHasChanged, render coalescing, duplicate renders, blazor performance |

> High-value page. "Blazor unnecessary re-renders" and "Blazor performance StateHasChanged" are
> active, well-trafficked search queries with weak existing answers.

**`/guides/cache`** — `Pages/Guides/Cache.razor`

| Field | Value |
|---|---|
| Title | `The cache — Deja` |
| Description | `Register AddDeja() and every keyed Blazor query taps a shared app-wide cache: instant renders from cached data, one fetch per key, and background revalidation when stale.` |
| Canonical | `.../Deja/guides/cache` |
| OG image | `og-guides.png` |
| Keywords | blazor cache, AddDeja, DejaClient, scoped cache blazor server, cache eviction, stale while revalidate |

**`/guides/query-keys`** — `Pages/Guides/QueryKeys.razor`

| Field | Value |
|---|---|
| Title | `Query keys — Deja` |
| Description | `A QueryKey is a structured, ordered cache identity for Blazor. Build hierarchical keys and prefix invalidation comes for free — invalidate a branch, refetch everything under it.` |
| Canonical | `.../Deja/guides/query-keys` |
| OG image | `og-guides.png` |
| Keywords | QueryKey, prefix invalidation, hierarchical cache key, StartsWith, canonical key, IQueryKeySegment |

**`/guides/cancellation`** — `Pages/Guides/Cancellation.razor`

| Field | Value |
|---|---|
| Title | `Cancellation — Deja` |
| Description | `Deja scopes every execution to the owning Blazor component's lifetime and cancels superseded loads automatically — usually with nothing to wire up yourself.` |
| Canonical | `.../Deja/guides/cancellation` |
| OG image | `og-guides.png` |
| Keywords | blazor cancellationtoken, cancel http request blazor, ComponentToken, supersede, blazor dispose cancel |

**`/guides/refetching`** — `Pages/Guides/Refetching.razor`

| Field | Value |
|---|---|
| Title | `Refetching & staleness — Deja` |
| Description | `Stale time decides when cached Blazor data needs revalidating; Refetch() is the explicit override that always fetches. Covers StaleTime, RefetchOnMount and background refetch.` |
| Canonical | `.../Deja/guides/refetching` |
| OG image | `og-guides.png` |
| Keywords | StaleTime, RefetchOnMount, blazor background refetch, stale while revalidate, manual refetch |

**`/guides/error-handling`** — `Pages/Guides/ErrorHandling.razor`

| Field | Value |
|---|---|
| Title | `Error handling — Deja` |
| Description | `Failures publish bindable error state in Blazor, run callbacks in a defined order, and are only rethrown when nobody observed them. Covers OnError and DisplayUserException.` |
| Canonical | `.../Deja/guides/error-handling` |
| OG image | `og-guides.png` |
| Keywords | blazor error handling, OnError, DisplayUserException, IsError, blazor retry, http timeout blazor |

---

### 6.5 API reference

API pages target long-tail, high-intent searches — a developer typing a type name into Google.
Descriptions should therefore **lead with the type name** so it appears at the front of the snippet.

**`/api/query`**

| Field | Value |
|---|---|
| Title | `Query<T> — Deja API` |
| Description | `Query<T> API reference: tracks a single asynchronous read and exposes its lifecycle as bindable state. Execute, Refetch, ClearData, Dispose, IsStale and IsCachedData.` |
| Canonical | `.../Deja/api/query` |
| Keywords | Query<T> blazor, Execute, Refetch, ClearData, IsStale, IsCachedData |

**`/api/query-parameters`**

| Field | Value |
|---|---|
| Title | `QueryParameters<T> — Deja API` |
| Description | `QueryParameters<T> API reference: describes one execution of a Query<T> — how to fetch and which callbacks to run. QueryFunction, QueryKey, StaleTime, Enabled and Select.` |
| Canonical | `.../Deja/api/query-parameters` |
| Keywords | QueryParameters, QueryFunction, StaleTime, Enabled, Select, query callbacks |

**`/api/refetch-parameters`**

| Field | Value |
|---|---|
| Title | `RefetchParameters<T> — Deja API` |
| Description | `RefetchParameters<T> API reference: the parameters a Query<T>.Refetch may override for that one call only. One-shot overrides of the execution's configuration.` |
| Canonical | `.../Deja/api/refetch-parameters` |
| Keywords | RefetchParameters, refetch override, one-shot parameters |

**`/api/mutation`**

| Field | Value |
|---|---|
| Title | `Mutation<T> — Deja API` |
| Description | `Mutation<T> API reference: tracks a single asynchronous write and exposes its lifecycle as bindable state, notifying its listener so the owning component re-renders.` |
| Canonical | `.../Deja/api/mutation` |
| Keywords | Mutation<T> blazor, Execute, IsLoading, mutation Data |

**`/api/mutation-parameters`**

| Field | Value |
|---|---|
| Title | `MutationParameters<T> — Deja API` |
| Description | `MutationParameters<T> API reference: describes one execution of a Mutation<T> — what to run and which callbacks to invoke. MutationFunction, VoidMutationFunction, InvalidateKeys.` |
| Canonical | `.../Deja/api/mutation-parameters` |
| Keywords | MutationParameters, MutationFunction, VoidMutationFunction, InvalidateKeys |

**`/api/deja-client`**

| Field | Value |
|---|---|
| Title | `DejaClient — Deja API` |
| Description | `DejaClient API reference: the shared query cache. A registry of entries keyed by QueryKey, plus invalidation, manual reads and writes, per-prefix defaults and eviction.` |
| Canonical | `.../Deja/api/deja-client` |
| Keywords | DejaClient, GetData, SetData, InvalidateAsync, RefetchAsync, Remove, Clear, SetDefaults, GetState |

**`/api/query-key`**

| Field | Value |
|---|---|
| Title | `QueryKey — Deja API` |
| Description | `QueryKey API reference: a structured, ordered cache key with value equality on a canonical form. Of, FromString, StartsWith and the IQueryKeySegment contract.` |
| Canonical | `.../Deja/api/query-key` |
| Keywords | QueryKey, QueryKey.Of, FromString, StartsWith, IQueryKeySegment, canonical form |

**`/api/deja-options`**

| Field | Value |
|---|---|
| Title | `DejaOptions — Deja API` |
| Description | `DejaOptions API reference: global cache defaults for Blazor, overridable per query via QueryParameters<T> and per key prefix via DejaClient.SetDefaults.` |
| Canonical | `.../Deja/api/deja-options` |
| Keywords | DejaOptions, DefaultStaleTime, DefaultCacheTime, MaxEntries, EvictionInterval, StructuralComparison, TimeProvider |

**`/api/deja-component-base`**

| Field | Value |
|---|---|
| Title | `DejaComponentBase — Deja API` |
| Description | `DejaComponentBase API reference: the Blazor base component that re-renders automatically when the Query<T> and Mutation<T> instances it owns change state.` |
| Canonical | `.../Deja/api/deja-component-base` |
| Keywords | DejaComponentBase, ComponentToken, Observe, Dispose, DisposeAsync, OnInitialized |

**`/api/enums`**

| Field | Value |
|---|---|
| Title | `Enums & options — Deja API` |
| Description | `Deja's smaller public types: RefetchOnMount, RefetchType, InvalidateOptions, QueryFilter, CacheEntryState, DisplayUserException and the observable contract.` |
| Canonical | `.../Deja/api/enums` |
| Keywords | RefetchOnMount, RefetchType, InvalidateOptions, QueryFilter, CacheEntryState, DisplayUserException, IDejaObservable |

---

### 6.6 Not found

**`/not-found`** — `Pages/NotFound.razor`

| Field | Value |
|---|---|
| Title | `Page not found — Deja` |
| Description | *(omit — no value in a 404 snippet)* |
| Robots | `<meta name="robots" content="noindex, follow" />` |
| Canonical | *(omit — never canonicalise a 404)* |

**Soft-404 problem.** The workflow copies `index.html` to `404.html`, so GitHub Pages serves the SPA
shell for unknown paths. GitHub Pages cannot return a real 404 status for that file, so Google may
index junk URLs. Mitigations, in order of preference:

1. Emit `<meta name="robots" content="noindex" />` into the generated `404.html` specifically —
   it is a separate file from `index.html`, so it can carry different tags. This is cheap and
   effective, and is the recommended fix.
2. Keep `sitemap.xml` authoritative and tightly scoped so crawlers have no reason to guess URLs.

---

## 7. `robots.txt` and `sitemap.xml`

### `wwwroot/robots.txt`

```
User-agent: *
Allow: /

Sitemap: https://lisihasaj.github.io/Deja/sitemap.xml
```

> Do not block `/_framework/`. Googlebot must fetch the Blazor runtime to render the page at all;
> blocking it guarantees an empty index entry.

### `wwwroot/sitemap.xml`

Generate at build time from `DocsNav.All` — it already enumerates every route in reading order, so
the sitemap can never drift from the navigation. Add the landing page manually; exclude `/not-found`.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"
        xmlns:xhtml="http://www.w3.org/1999/xhtml">
  <url>
    <loc>https://lisihasaj.github.io/Deja/</loc>
    <lastmod>2026-08-16</lastmod>
    <changefreq>weekly</changefreq>
    <priority>1.0</priority>
  </url>
  <url>
    <loc>https://lisihasaj.github.io/Deja/getting-started/quick-start</loc>
    <lastmod>2026-08-16</lastmod>
    <changefreq>monthly</changefreq>
    <priority>0.9</priority>
  </url>
  <!-- … one entry per DocsNav route … -->
</urlset>
```

Suggested priorities: landing `1.0`; quick start and introduction `0.9`; installation and demos
`0.8`; guides `0.7`; API reference `0.6`.

Set `lastmod` from the git commit date of each page's `.razor` file — accurate `lastmod` values are
used by Google for recrawl scheduling, and fabricated ones are ignored or distrusted.

Once German URLs exist (§3), add `<xhtml:link rel="alternate" hreflang="…">` entries inside each
`<url>` block rather than listing the German pages separately.

---

## 8. Structured data (JSON-LD)

Add to the landing page:

```json
{
  "@context": "https://schema.org",
  "@type": "SoftwareSourceCode",
  "name": "Deja",
  "description": "Data fetching, mutation and caching primitives for Blazor — WebAssembly and Server.",
  "url": "https://lisihasaj.github.io/Deja/",
  "codeRepository": "https://github.com/lisihasaj/Deja",
  "programmingLanguage": "C#",
  "runtimePlatform": ".NET",
  "license": "https://opensource.org/licenses/MIT",
  "author": { "@type": "Person", "name": "Lisjan Hasaj" }
}
```

Add to **every** documentation page — breadcrumbs are the most reliably granted rich result and
visibly improve the SERP listing:

```json
{
  "@context": "https://schema.org",
  "@type": "BreadcrumbList",
  "itemListElement": [
    { "@type": "ListItem", "position": 1, "name": "Deja", "item": "https://lisihasaj.github.io/Deja/" },
    { "@type": "ListItem", "position": 2, "name": "Guides", "item": "https://lisihasaj.github.io/Deja/guides/queries" },
    { "@type": "ListItem", "position": 3, "name": "Queries" }
  ]
}
```

The section and page names come straight from `DocsNav` (`NavItem.Section` and `NavItem.Title`), so
this can be generated rather than hand-written. Note the last item correctly omits `item` — it is
the current page.

Also worth adding: `TechArticle` on guide pages, `HowTo` on installation and quick start (§6.2).

---

## 9. Implementation approach

### Where the metadata lives

Extend `NavItem` in `Navigation/DocsNav.cs` with description fields, so structure and metadata stay
in one place — consistent with the file's existing role as "the single source of truth":

```csharp
public sealed record NavItem(
    string Title,
    string TitleDe,
    string Href,
    string Section,
    string SectionDe,
    string? Badge = null,
    string[]? Keywords = null,
    string? Description = null,
    string? DescriptionDe = null,
    string? OgImage = null);
```

The landing page and `/not-found` are not in `DocsNav`; handle those two as explicit special cases.

### Runtime tags — a reusable component

Add a `Components/PageMeta.razor` that renders `<HeadContent>` from a route lookup, then place one
line on each page:

```razor
<PageMeta />
```

It resolves the current route via `NavigationManager`, looks the entry up in `DocsNav`, and emits
the description, canonical, `og:*`, `twitter:*` and JSON-LD blocks. This means 26 one-line edits
rather than 26 hand-maintained metadata blocks, and no risk of the tags drifting from the nav.

`<PageTitle>` stays where it is on each page — it already works and reads clearly at the top of each
file.

### Build-time tags — the generator

A script invoked from `.github/workflows/docs.yml` after `dotnet publish`:

1. Read the route + metadata manifest (emit it as JSON from the docs project, or duplicate the small
   table in the script).
2. For each route, write `publish/wwwroot/<route>/index.html` — `index.html` with the placeholder
   head block replaced by that route's real tags.
3. Write `sitemap.xml` and `robots.txt`.
4. Inject `noindex` into `404.html`.
5. Rewrite `<base href>` and all absolute URLs for the `/Deja/` subpath (the existing `sed` step
   folds into this).

---

## 10. Off-page and verification

These matter as much as the tags — a technically perfect page with no inbound links does not rank.

1. **Google Search Console** — verify the property, submit `sitemap.xml`, and watch the
   *Page indexing* report. This is the only way to confirm Googlebot actually renders the WASM app.
   Use *URL Inspection → Test live URL → View rendered page* on one deep route to check.
2. **Bing Webmaster Tools** — free, and can import directly from Search Console. Bing renders JS far
   less reliably, so this is where the prerender work pays off visibly.
3. **Link the docs site from everywhere it belongs**: the GitHub repo's *About* sidebar (website
   field), the README's opening lines, and — importantly — `<PackageProjectUrl>` in
   `src/Deja/Deja.csproj`, which currently points at the GitHub repo. NuGet.org is a high-authority
   domain; a link from the package page is one of the strongest signals available here.
4. **Consider a custom domain** (e.g. `deja.dev`). `lisihasaj.github.io/Deja/` inherits none of
   github.io's authority for a subpath project and is harder to recall and share. Worth doing before
   the site accumulates links, since migrating later costs redirect complexity.
5. **`CHANGELOG.md` as a page.** Release notes are a recurring, freshness-signalling content type
   that developers search for by version number. Consider rendering it as a route.

### Validation checklist

| Tool | Checks |
|---|---|
| Facebook Sharing Debugger | OG tags, image rendering, cache refresh |
| X/Twitter Card Validator | Card type and image |
| LinkedIn Post Inspector | OG tags (LinkedIn caches aggressively — inspect to bust it) |
| Google Rich Results Test | JSON-LD validity and rich-result eligibility |
| Google Search Console → URL Inspection | Whether the rendered DOM contains the content |
| Lighthouse SEO audit | Titles, descriptions, crawlability, mobile |
| `curl -A "facebookexternalhit/1.1" <url>` | **The decisive test** — proves what a non-JS crawler actually receives |

That last check is the one that matters most. If `curl` with a social-crawler user agent does not
show the page's `og:title`, no social platform will show a card, regardless of what the site renders
in a browser.

---

## 11. Priority order

| # | Item | Impact | Effort |
|---|---|---|---|
| 1 | `og-default.png` + global OG/Twitter tags in `index.html` | **High** — every shared link gets a card immediately | Low |
| 2 | `robots.txt` + `sitemap.xml` generated from `DocsNav` | **High** — makes 26 pages discoverable at all | Low |
| 3 | Per-page descriptions + canonicals via `PageMeta.razor` | **High** | Medium |
| 4 | Build-time prerender of per-route `<head>` (§2) | **High** — the fix that makes 1 and 3 work for non-JS crawlers | Medium |
| 5 | `noindex` on `404.html` | Medium — prevents junk indexing | Low |
| 6 | JSON-LD: `SoftwareSourceCode` + `BreadcrumbList` | Medium | Low |
| 7 | Search Console + Bing Webmaster verification | Medium — measurement, not ranking | Low |
| 8 | `PackageProjectUrl` → docs site, README link, repo About field | Medium — real inbound authority | Low |
| 9 | Section-specific and per-page OG images | Medium — social CTR | Medium |
| 10 | Language-prefixed URLs + `hreflang` (§3) | Medium — unlocks the German half of the site | High |
| 11 | Custom domain | Medium — long-term | Medium |
| 12 | PWA manifest, apple touch icon, theme-color | Low — polish | Low |

**Items 1–3 are a single afternoon and deliver most of the available benefit.** Item 4 is what makes
the site genuinely competitive in search and reliable on social.
