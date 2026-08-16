using Deja.Docs.Services;

namespace Deja.Docs.Navigation;

public sealed record NavItem(string Title, string TitleDe, string Href, string Section, string SectionDe, string? Badge = null, string[]? Keywords = null)
{
    public string TitleFor(Language lang) => lang == Language.De ? TitleDe : Title;

    public string SectionFor(Language lang) => lang == Language.De ? SectionDe : Section;
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

        void Add(int section, string title, string titleDe, string href, string? badge = null, params string[] keywords)
            => sections[section].Links.Add(new NavItem(title, titleDe, href, sections[section].Section, sections[section].SectionDe, badge, keywords));

        Add(0, "Introduction", "Einführung", "getting-started/introduction", null, "why", "boilerplate", "about", "overview");
        Add(0, "Installation", "Installation", "getting-started/installation", null, "nuget", "install", "AddDeja", "setup", "blazor server", "webassembly");
        Add(0, "Quick start", "Schnellstart", "getting-started/quick-start", null, "tutorial", "first query", "example");

        Add(1, "Todo list", "Todo-Liste", "demos/todo-list", "live", "crud", "create", "update", "delete", "mutation");
        Add(1, "Shared cache", "Gemeinsamer Cache", "demos/shared-cache", "live", "deduplication", "subscribers", "one fetch");
        Add(1, "Isolation", "Isolation", "demos/isolation", "live", "re-render", "single listener", "sibling");
        Add(1, "Optimistic write", "Optimistisches Schreiben", "demos/optimistic-write", "live", "SetData", "rollback", "updater");

        Add(2, "Queries", "Queries", "guides/queries", null, "Execute", "IsLoading", "Data", "Enabled", "Select", "PlaceholderData", "overloads");
        Add(2, "Mutations", "Mutations", "guides/mutations", null, "Execute", "InvalidateKeys", "write", "post", "void");
        Add(2, "Component base", "Komponenten-Basisklasse", "guides/component-base", null, "DejaComponentBase", "Observe", "dispose", "OnInitialized", "re-render", "StateHasChanged");
        Add(2, "Rendering & re-renders", "Rendering & Re-Renders", "guides/rendering", null, "re-render", "render count", "coalescing", "StateHasChanged", "performance", "notifications", "batching", "duplicate renders");
        Add(2, "The cache", "Der Cache", "guides/cache", null, "AddDeja", "DejaClient", "scoped", "entry", "eviction", "blazor server");
        Add(2, "Query keys", "Query-Keys", "guides/query-keys", null, "QueryKey", "Of", "prefix", "StartsWith", "dictionary", "IQueryKeySegment", "anonymous");
        Add(2, "Cancellation", "Abbruch", "guides/cancellation", null, "CancellationToken", "ComponentToken", "supersede", "abort", "dispose");
        Add(2, "Refetching & staleness", "Refetching & Aktualität", "guides/refetching", null, "Refetch", "StaleTime", "RefetchOnMount", "stale", "fresh", "background");
        Add(2, "Error handling", "Fehlerbehandlung", "guides/error-handling", null, "OnError", "DisplayUserException", "IsError", "retry", "timeout");

        Add(3, "Query<T>", "Query<T>", "api/query", null, "Execute", "Refetch", "ClearData", "Dispose", "IsStale", "IsCachedData");
        Add(3, "QueryParameters<T>", "QueryParameters<T>", "api/query-parameters", null, "QueryFunction", "QueryKey", "callbacks", "StaleTime", "Enabled", "Select");
        Add(3, "RefetchParameters<T>", "RefetchParameters<T>", "api/refetch-parameters", null, "override", "one-shot");
        Add(3, "Mutation<T>", "Mutation<T>", "api/mutation", null, "Execute", "IsLoading", "Data");
        Add(3, "MutationParameters<T>", "MutationParameters<T>", "api/mutation-parameters", null, "MutationFunction", "VoidMutationFunction", "InvalidateKeys");
        Add(3, "DejaClient", "DejaClient", "api/deja-client", null, "GetData", "SetData", "InvalidateAsync", "RefetchAsync", "Remove", "Clear", "SetDefaults", "GetState");
        Add(3, "QueryKey", "QueryKey", "api/query-key", null, "Of", "FromString", "StartsWith", "IQueryKeySegment", "canonical");
        Add(3, "DejaOptions", "DejaOptions", "api/deja-options", null, "DefaultStaleTime", "DefaultCacheTime", "MaxEntries", "EvictionInterval", "StructuralComparison", "TimeProvider", "QueryDefaults");
        Add(3, "DejaComponentBase", "DejaComponentBase", "api/deja-component-base", null, "ComponentToken", "Observe", "Dispose", "DisposeAsync", "OnInitialized");
        Add(3, "Enums & options", "Enums & Optionen", "api/enums", null, "RefetchOnMount", "RefetchType", "InvalidateOptions", "QueryFilter", "CacheEntryState", "DisplayUserException");

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
