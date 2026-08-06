namespace Deja.Sample.Services;

/// <summary>A todo item served by the fake backend.</summary>
public sealed record Todo(int Id, string Title);

/// <summary>
/// In-memory stand-in for an HTTP API, with artificial latency so the query and mutation
/// lifecycle states are visible, and a switch to simulate a failing fetch.
/// </summary>
public sealed class FakeTodoApi
{
    private readonly List<Todo> _todos =
    [
        new(1, "Extract Query<T> from the app"),
        new(2, "Extract Mutation<T> from the app"),
        new(3, "Design the QueryClient cache"),
    ];

    private int _nextId = 4;

    /// <summary>When true, the next fetch fails with a user-displayable error.</summary>
    public bool FailNextFetch { get; set; }

    /// <summary>Returns all todos after a simulated network delay.</summary>
    public async Task<IReadOnlyList<Todo>> GetTodosAsync(CancellationToken token)
    {
        await Task.Delay(900, token);

        if (FailNextFetch)
        {
            FailNextFetch = false;
            throw new DisplayUserException("The todo service is temporarily unavailable. Try refetching.");
        }

        return [.. _todos];
    }

    /// <summary>Adds a todo after a simulated network delay.</summary>
    public async Task<Todo> AddTodoAsync(string title)
    {
        await Task.Delay(600);

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DisplayUserException("A todo needs a title.");
        }

        var todo = new Todo(_nextId++, title.Trim());
        _todos.Add(todo);
        return todo;
    }
}
