namespace Deja.Docs.Navigation;

public sealed record NavItem(string Title, string Href, string Section, string? Badge = null, string[]? Keywords = null);

public sealed record NavSection(string Title, IReadOnlyList<NavItem> Links);

/// <summary>
/// The single source of truth for the site's structure: the sidebar, the prev/next footer and the
/// search index all read from this list. Adding a page is one entry here.
/// </summary>
public static class DocsNav
{
    public static IReadOnlyList<NavSection> Sections { get; } = Build();

    private static List<NavSection> Build()
    {
        List<(string Section, List<NavItem> Links)> sections =
        [
            ("Getting started", []),
            ("Live demos", []),
            ("Guides", []),
            ("API reference", []),
        ];

        void Add(int section, string title, string href, string? badge = null, params string[] keywords)
            => sections[section].Links.Add(new NavItem(title, href, sections[section].Section, badge, keywords));

        Add(0, "Introduction", "getting-started/introduction", null, "why", "boilerplate", "about", "overview");
        Add(0, "Installation", "getting-started/installation", null, "nuget", "install", "AddDeja", "setup", "blazor server", "webassembly");
        Add(0, "Quick start", "getting-started/quick-start", null, "tutorial", "first query", "example");

        Add(1, "Todo list", "demos/todo-list", "live", "crud", "create", "update", "delete", "mutation");
        Add(1, "Shared cache", "demos/shared-cache", "live", "deduplication", "subscribers", "one fetch");
        Add(1, "Isolation", "demos/isolation", "live", "re-render", "single listener", "sibling");
        Add(1, "Optimistic write", "demos/optimistic-write", "live", "SetData", "rollback", "updater");

        Add(2, "Queries", "guides/queries", null, "Execute", "IsLoading", "Data", "Enabled", "Select", "PlaceholderData", "overloads");
        Add(2, "Mutations", "guides/mutations", null, "Execute", "InvalidateKeys", "write", "post", "void");
        Add(2, "Component base", "guides/component-base", null, "DejaComponentBase", "Observe", "dispose", "OnInitialized", "re-render", "StateHasChanged");
        Add(2, "The cache", "guides/cache", null, "AddDeja", "DejaClient", "scoped", "entry", "eviction", "blazor server");
        Add(2, "Query keys", "guides/query-keys", null, "QueryKey", "Of", "prefix", "StartsWith", "dictionary", "IQueryKeySegment", "anonymous");
        Add(2, "Cancellation", "guides/cancellation", null, "CancellationToken", "ComponentToken", "supersede", "abort", "dispose");
        Add(2, "Refetching & staleness", "guides/refetching", null, "Refetch", "StaleTime", "RefetchOnMount", "stale", "fresh", "background");
        Add(2, "Error handling", "guides/error-handling", null, "OnError", "DisplayUserException", "IsError", "retry", "timeout");

        Add(3, "Query<T>", "api/query", null, "Execute", "Refetch", "ClearData", "Dispose", "IsStale", "IsCachedData");
        Add(3, "QueryParameters<T>", "api/query-parameters", null, "QueryFunction", "QueryKey", "callbacks", "StaleTime", "Enabled", "Select");
        Add(3, "RefetchParameters<T>", "api/refetch-parameters", null, "override", "one-shot");
        Add(3, "Mutation<T>", "api/mutation", null, "Execute", "IsLoading", "Data");
        Add(3, "MutationParameters<T>", "api/mutation-parameters", null, "MutationFunction", "VoidMutationFunction", "InvalidateKeys");
        Add(3, "DejaClient", "api/deja-client", null, "GetData", "SetData", "InvalidateAsync", "RefetchAsync", "Remove", "Clear", "SetDefaults", "GetState");
        Add(3, "QueryKey", "api/query-key", null, "Of", "FromString", "StartsWith", "IQueryKeySegment", "canonical");
        Add(3, "DejaOptions", "api/deja-options", null, "DefaultStaleTime", "DefaultCacheTime", "MaxEntries", "EvictionInterval", "StructuralComparison", "TimeProvider", "QueryDefaults");
        Add(3, "DejaComponentBase", "api/deja-component-base", null, "ComponentToken", "Observe", "Dispose", "DisposeAsync", "OnInitialized");
        Add(3, "Enums & options", "api/enums", null, "RefetchOnMount", "RefetchType", "InvalidateOptions", "QueryFilter", "CacheEntryState", "DisplayUserException");

        return [.. sections.Select(s => new NavSection(s.Section, s.Links))];
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
            var haystack = link.Title + ' ' + link.Section + ' ' + string.Join(' ', link.Keywords ?? []);
            if (terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                yield return link;
            }
        }
    }
}
