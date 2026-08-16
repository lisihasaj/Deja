using Deja.Docs.Services;

namespace Deja.Docs.Navigation;

public sealed record NavItem(
    string Title,
    string TitleDe,
    string Href,
    string Section,
    string SectionDe,
    string Description,
    string DescriptionDe,
    string? Badge = null,
    string[]? Keywords = null)
{
    public string TitleFor(Language lang) => lang == Language.De ? TitleDe : Title;

    public string SectionFor(Language lang) => lang == Language.De ? SectionDe : Section;

    public string DescriptionFor(Language lang) => lang == Language.De ? DescriptionDe : Description;
}

public sealed record NavSection(string Title, string TitleDe, IReadOnlyList<NavItem> Links)
{
    public string TitleFor(Language lang) => lang == Language.De ? TitleDe : Title;
}

/// <summary>
/// The single source of truth for the site's structure: the sidebar, the prev/next footer and the
/// search index all read from this list. Adding a page is one entry here.
/// </summary>
public static class DocsNav
{
    public static IReadOnlyList<NavSection> Sections { get; } = Build();

    private static List<NavSection> Build()
    {
        List<(string Section, string SectionDe, List<NavItem> Links)> sections =
        [
            ("Getting started", "Erste Schritte", []),
            ("Live demos", "Live-Demos", []),
            ("Guides", "Anleitungen", []),
            ("API reference", "API-Referenz", []),
        ];

        void Add(int section, string title, string titleDe, string href, string description, string descriptionDe, string? badge = null, params string[] keywords)
            => sections[section].Links.Add(new NavItem(title, titleDe, href, sections[section].Section, sections[section].SectionDe, description, descriptionDe, badge, keywords));

        Add(0, "Introduction", "Einführung", "getting-started/introduction",
            "Deja removes the boilerplate around calling an API in Blazor, caches what you fetched, and keeps that data synchronized across every component showing it.",
            "Deja beseitigt den Boilerplate-Code rund um API-Aufrufe in Blazor, cacht die geladenen Daten und hält sie über alle Komponenten hinweg synchron, die sie anzeigen.",
            null, "why", "boilerplate", "about", "overview");
        Add(0, "Installation", "Installation", "getting-started/installation",
            "Install the Deja NuGet package and register the shared query cache with AddDeja(). Multi-targets net8.0, net9.0 and net10.0 on Blazor WebAssembly and Server.",
            "Installiere das Deja-NuGet-Paket und registriere den gemeinsamen Query-Cache mit AddDeja(). Multi-Targeting für net8.0, net9.0 und net10.0 auf Blazor WebAssembly und Server.",
            null, "nuget", "install", "AddDeja", "setup", "blazor server", "webassembly");
        Add(0, "Quick start", "Schnellstart", "getting-started/quick-start",
            "From zero to a cached, cancellable, auto-rendering Blazor query in three steps: declare and execute it, bind its state, then write with a mutation and invalidate the key.",
            "In drei Schritten von null zu einer gecachten, abbrechbaren, automatisch rendernden Blazor-Query: deklarieren und ausführen, den Zustand binden, dann per Mutation schreiben und den Key invalidieren.",
            null, "tutorial", "first query", "example");

        Add(1, "Todo list", "Todo-Liste", "demos/todo-list",
            "Full CRUD against a live API in Blazor: a keyed query for the list, three mutations for create, update and delete, and SetData applying results to the shared cache.",
            "Vollständiges CRUD gegen eine echte API in Blazor: eine Query mit Key für die Liste, drei Mutations für Anlegen, Ändern und Löschen, und SetData schreibt Ergebnisse in den gemeinsamen Cache.",
            "live", "crud", "create", "update", "delete", "mutation");
        Add(1, "Shared cache", "Gemeinsamer Cache", "demos/shared-cache",
            "Two sibling Blazor components execute the same query key. One fetch serves both, a refetch from either updates both, and the inspector shows the single shared cache entry.",
            "Zwei benachbarte Blazor-Komponenten führen denselben Query-Key aus. Ein Fetch versorgt beide, ein Refetch von einer aktualisiert beide, und der Inspector zeigt den einen gemeinsamen Cache-Eintrag.",
            "live", "deduplication", "subscribers", "one fetch");
        Add(1, "Isolation", "Isolation", "demos/isolation",
            "Two unkeyed sibling Blazor components that provably cannot re-render each other: Deja state notifies exactly one listener — the component that owns it.",
            "Zwei benachbarte Blazor-Komponenten ohne Key, die einander nachweislich nicht neu rendern können: Deja-Zustand benachrichtigt genau einen Listener — die Komponente, der er gehört.",
            "live", "re-render", "single listener", "sibling");
        Add(1, "Optimistic write", "Optimistisches Schreiben", "demos/optimistic-write",
            "Flip the Blazor UI immediately with SetData's updater form, run the request after, and roll back to a snapshot if it fails. Optimistic updates with rollback.",
            "Aktualisiere die Blazor-UI sofort mit der Updater-Form von SetData, führe den Request danach aus und stelle bei einem Fehler den Snapshot wieder her. Optimistische Updates mit Rollback.",
            "live", "SetData", "rollback", "updater");

        Add(2, "Queries", "Queries", "guides/queries",
            "A Query<T> tracks a single asynchronous read in Blazor and exposes its lifecycle as bindable state. The three ways to run one and the parameters that shape an execution.",
            "Eine Query<T> verfolgt einen einzelnen asynchronen Lesevorgang in Blazor und stellt dessen Lebenszyklus als bindbaren Zustand bereit. Die drei Ausführungswege und die Parameter einer Ausführung.",
            null, "Execute", "IsLoading", "Data", "Enabled", "Select", "PlaceholderData", "overloads");
        Add(2, "Mutations", "Mutations", "guides/mutations",
            "A Mutation<T> gives Blazor writes the same bindable treatment as reads — IsLoading, IsError, ErrorMessage, Data — plus automatic cache invalidation on success.",
            "Eine Mutation<T> behandelt Schreibvorgänge in Blazor genauso bindbar wie Lesevorgänge — IsLoading, IsError, ErrorMessage, Data — plus automatische Cache-Invalidierung bei Erfolg.",
            null, "Execute", "InvalidateKeys", "write", "post", "void");
        Add(2, "Component base", "Komponenten-Basisklasse", "guides/component-base",
            "DejaComponentBase discovers the queries and mutations a Blazor component owns, re-renders it when they change, scopes them to its lifetime, and cleans up on dispose.",
            "DejaComponentBase erkennt die Queries und Mutations einer Blazor-Komponente, rendert sie bei Änderungen neu, bindet sie an ihre Lebensdauer und räumt beim Dispose auf.",
            null, "DejaComponentBase", "Observe", "dispose", "OnInitialized", "re-render", "StateHasChanged");
        Add(2, "Rendering & re-renders", "Rendering & Re-Renders", "guides/rendering",
            "A Blazor component should re-render when what it displays changes, and not otherwise. How Deja enforces that with one notification per transition and coalesced renders.",
            "Eine Blazor-Komponente sollte neu rendern, wenn sich ändert, was sie anzeigt — und sonst nicht. Wie Deja das mit einer Benachrichtigung pro Übergang und zusammengefassten Renders erzwingt.",
            null, "re-render", "render count", "coalescing", "StateHasChanged", "performance", "notifications", "batching", "duplicate renders");
        Add(2, "The cache", "Der Cache", "guides/cache",
            "Register AddDeja() and every keyed Blazor query taps a shared app-wide cache: instant renders from cached data, one fetch per key, and background revalidation when stale.",
            "Registriere AddDeja() und jede Blazor-Query mit Key nutzt einen gemeinsamen, appweiten Cache: sofortige Renders aus gecachten Daten, ein Fetch pro Key und Revalidierung im Hintergrund.",
            null, "AddDeja", "DejaClient", "scoped", "entry", "eviction", "blazor server");
        Add(2, "Query keys", "Query-Keys", "guides/query-keys",
            "A QueryKey is a structured, ordered cache identity for Blazor. Build hierarchical keys and prefix invalidation comes for free — invalidate a branch, refetch everything under it.",
            "Ein QueryKey ist eine strukturierte, geordnete Cache-Identität für Blazor. Baue hierarchische Keys und Prefix-Invalidierung gibt es gratis — invalidiere einen Zweig, alles darunter lädt neu.",
            null, "QueryKey", "Of", "prefix", "StartsWith", "dictionary", "IQueryKeySegment", "anonymous");
        Add(2, "Cancellation", "Abbruch", "guides/cancellation",
            "Deja scopes every execution to the owning Blazor component's lifetime and cancels superseded loads automatically — usually with nothing to wire up yourself.",
            "Deja bindet jede Ausführung an die Lebensdauer der besitzenden Blazor-Komponente und bricht verdrängte Ladevorgänge automatisch ab — meist ganz ohne eigene Verdrahtung.",
            null, "CancellationToken", "ComponentToken", "supersede", "abort", "dispose");
        Add(2, "Refetching & staleness", "Refetching & Aktualität", "guides/refetching",
            "Stale time decides when cached Blazor data needs revalidating; Refetch() is the explicit override that always fetches. Covers StaleTime, RefetchOnMount and background refetch.",
            "Die Stale-Zeit entscheidet, wann gecachte Blazor-Daten revalidiert werden; Refetch() ist der explizite Override, der immer lädt. Behandelt StaleTime, RefetchOnMount und Hintergrund-Refetch.",
            null, "Refetch", "StaleTime", "RefetchOnMount", "stale", "fresh", "background");
        Add(2, "Error handling", "Fehlerbehandlung", "guides/error-handling",
            "Failures publish bindable error state in Blazor, run callbacks in a defined order, and are only rethrown when nobody observed them. Covers OnError and DisplayUserException.",
            "Fehler veröffentlichen bindbaren Fehlerzustand in Blazor, rufen Callbacks in definierter Reihenfolge auf und werden nur rethrown, wenn niemand sie beobachtet hat. Mit OnError und DisplayUserException.",
            null, "OnError", "DisplayUserException", "IsError", "retry", "timeout");

        Add(3, "Query<T>", "Query<T>", "api/query",
            "Query<T> API reference: tracks a single asynchronous read and exposes its lifecycle as bindable state. Execute, Refetch, ClearData, Dispose, IsStale and IsCachedData.",
            "Query<T>-API-Referenz: verfolgt einen einzelnen asynchronen Lesevorgang und stellt dessen Lebenszyklus als bindbaren Zustand bereit. Execute, Refetch, ClearData, Dispose, IsStale und IsCachedData.",
            null, "Execute", "Refetch", "ClearData", "Dispose", "IsStale", "IsCachedData");
        Add(3, "QueryParameters<T>", "QueryParameters<T>", "api/query-parameters",
            "QueryParameters<T> API reference: describes one execution of a Query<T> — how to fetch and which callbacks to run. QueryFunction, QueryKey, StaleTime, Enabled and Select.",
            "QueryParameters<T>-API-Referenz: beschreibt eine Ausführung einer Query<T> — wie geladen wird und welche Callbacks laufen. QueryFunction, QueryKey, StaleTime, Enabled und Select.",
            null, "QueryFunction", "QueryKey", "callbacks", "StaleTime", "Enabled", "Select");
        Add(3, "RefetchParameters<T>", "RefetchParameters<T>", "api/refetch-parameters",
            "RefetchParameters<T> API reference: the parameters a Query<T>.Refetch may override for that one call only. One-shot overrides of the execution's configuration.",
            "RefetchParameters<T>-API-Referenz: die Parameter, die Query<T>.Refetch für genau diesen einen Aufruf überschreiben darf. Einmalige Overrides der Konfiguration einer Ausführung.",
            null, "override", "one-shot");
        Add(3, "Mutation<T>", "Mutation<T>", "api/mutation",
            "Mutation<T> API reference: tracks a single asynchronous write and exposes its lifecycle as bindable state, notifying its listener so the owning component re-renders.",
            "Mutation<T>-API-Referenz: verfolgt einen einzelnen asynchronen Schreibvorgang und stellt dessen Lebenszyklus als bindbaren Zustand bereit — die besitzende Komponente rendert sich neu.",
            null, "Execute", "IsLoading", "Data");
        Add(3, "MutationParameters<T>", "MutationParameters<T>", "api/mutation-parameters",
            "MutationParameters<T> API reference: describes one execution of a Mutation<T> — what to run and which callbacks to invoke. MutationFunction, VoidMutationFunction, InvalidateKeys.",
            "MutationParameters<T>-API-Referenz: beschreibt eine Ausführung einer Mutation<T> — was läuft und welche Callbacks aufgerufen werden. MutationFunction, VoidMutationFunction, InvalidateKeys.",
            null, "MutationFunction", "VoidMutationFunction", "InvalidateKeys");
        Add(3, "DejaClient", "DejaClient", "api/deja-client",
            "DejaClient API reference: the shared query cache. A registry of entries keyed by QueryKey, plus invalidation, manual reads and writes, per-prefix defaults and eviction.",
            "DejaClient-API-Referenz: der gemeinsame Query-Cache. Ein Register von Einträgen je QueryKey, plus Invalidierung, manuelle Lese- und Schreibzugriffe, Prefix-Defaults und Eviction.",
            null, "GetData", "SetData", "InvalidateAsync", "RefetchAsync", "Remove", "Clear", "SetDefaults", "GetState");
        Add(3, "QueryKey", "QueryKey", "api/query-key",
            "QueryKey API reference: a structured, ordered cache key with value equality on a canonical form. Of, FromString, StartsWith and the IQueryKeySegment contract.",
            "QueryKey-API-Referenz: ein strukturierter, geordneter Cache-Key mit Wertgleichheit über eine kanonische Form. Of, FromString, StartsWith und der IQueryKeySegment-Vertrag.",
            null, "Of", "FromString", "StartsWith", "IQueryKeySegment", "canonical");
        Add(3, "DejaOptions", "DejaOptions", "api/deja-options",
            "DejaOptions API reference: global cache defaults for Blazor, overridable per query via QueryParameters<T> and per key prefix via DejaClient.SetDefaults.",
            "DejaOptions-API-Referenz: globale Cache-Defaults für Blazor, überschreibbar pro Query via QueryParameters<T> und pro Key-Prefix via DejaClient.SetDefaults.",
            null, "DefaultStaleTime", "DefaultCacheTime", "MaxEntries", "EvictionInterval", "StructuralComparison", "TimeProvider", "QueryDefaults");
        Add(3, "DejaComponentBase", "DejaComponentBase", "api/deja-component-base",
            "DejaComponentBase API reference: the Blazor base component that re-renders automatically when the Query<T> and Mutation<T> instances it owns change state.",
            "DejaComponentBase-API-Referenz: die Blazor-Basiskomponente, die automatisch neu rendert, wenn ihre Query<T>- und Mutation<T>-Instanzen den Zustand wechseln.",
            null, "ComponentToken", "Observe", "Dispose", "DisposeAsync", "OnInitialized");
        Add(3, "Enums & options", "Enums & Optionen", "api/enums",
            "Deja's smaller public types: RefetchOnMount, RefetchType, InvalidateOptions, QueryFilter, CacheEntryState, DisplayUserException and the observable contract.",
            "Dejas kleinere öffentliche Typen: RefetchOnMount, RefetchType, InvalidateOptions, QueryFilter, CacheEntryState, DisplayUserException und der Observable-Vertrag.",
            null, "RefetchOnMount", "RefetchType", "InvalidateOptions", "QueryFilter", "CacheEntryState", "DisplayUserException");

        return [.. sections.Select(s => new NavSection(s.Section, s.SectionDe, s.Links))];
    }

    private static readonly Lazy<List<NavItem>> _flat = new(() => [.. Sections.SelectMany(s => s.Links)]);

    /// <summary>All links in reading order, for prev/next and search.</summary>
    public static IReadOnlyList<NavItem> All => _flat.Value;

    /// <summary>The links around <paramref name="href"/> in reading order.</summary>
    public static (NavItem? Prev, NavItem? Next) Around(string href)
    {
        var flat = _flat.Value;
        var index = flat.FindIndex(l => string.Equals(l.Href, href, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return (null, null);

        return (index > 0 ? flat[index - 1] : null, index < flat.Count - 1 ? flat[index + 1] : null);
    }

    public static IEnumerable<NavItem> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var link in All)
        {
            var haystack = link.Title + ' ' + link.TitleDe + ' ' + link.Section + ' ' + link.SectionDe + ' ' + string.Join(' ', link.Keywords ?? []);
            if (terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                yield return link;
            }
        }
    }
}
