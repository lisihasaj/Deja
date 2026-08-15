namespace Deja.Docs.Services;

/// <summary>
/// Every cache key the demos use, in one place. Hierarchical on purpose: invalidating the
/// <c>Todos</c> prefix hits the lists and the details in one call.
/// </summary>
public static class DocsKeys
{
    public static QueryKey Todos => QueryKey.Of("todos");

    public static QueryKey TodoList(int limit) => QueryKey.Of("todos", "list", limit);

    public static QueryKey TodoDetail(int id) => QueryKey.Of("todos", "detail", id);

    public static QueryKey Posts(int? userId, int page) => QueryKey.Of(
        "posts", "list", new Dictionary<string, object?> { ["userId"] = userId, ["page"] = page });

    public static QueryKey User(int id) => QueryKey.Of("users", "detail", id);
}
