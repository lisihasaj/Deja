using System.Net.Http.Json;

namespace Deja.Docs.Services;

/// <summary>
/// Typed client for JSONPlaceholder. Every method takes a <see cref="CancellationToken"/>, so the
/// cancellation docs are demonstrable rather than merely described. Writes are real requests, but
/// JSONPlaceholder fakes persistence — a demo can hammer them and always reset clean.
/// </summary>
/// <remarks>
/// When the API is unreachable, reads fall back to <see cref="SeedData"/> and
/// <see cref="IsOffline"/> is raised so the demo controls can show an "offline sample data" badge.
/// A failure armed via <see cref="FailureSwitch"/> is thrown before the request and is never
/// converted into fallback data.
/// </remarks>
public sealed class JsonPlaceholderApi(HttpClient http, LatencySimulator latency, FailureSwitch failure)
{
    private const int PageSize = 10;

    public bool IsOffline { get; private set; }

    public event Action? OfflineChanged;

    public Task<IReadOnlyList<TodoDto>> GetTodosAsync(int limit, CancellationToken token) => RunAsync(
        async t => (IReadOnlyList<TodoDto>)(await http.GetFromJsonAsync<List<TodoDto>>($"todos?_limit={limit}", t))!,
        () => [.. SeedData.Todos.Take(limit)],
        token);

    public Task<IReadOnlyList<TodoDto>> GetTodosByUserAsync(int userId, CancellationToken token) => RunAsync(
        async t => (IReadOnlyList<TodoDto>)(await http.GetFromJsonAsync<List<TodoDto>>($"todos?userId={userId}", t))!,
        () => [.. SeedData.Todos.Where(todo => todo.UserId == userId)],
        token);

    public Task<TodoDto> GetTodoAsync(int id, CancellationToken token) => RunAsync(
        async t => (await http.GetFromJsonAsync<TodoDto>($"todos/{id}", t))!,
        () => SeedData.Todos.FirstOrDefault(todo => todo.Id == id) ?? new TodoDto(id, 1, $"todo #{id}", false),
        token);

    public Task<IReadOnlyList<PostDto>> GetPostsAsync(int? userId, int page, CancellationToken token) => RunAsync(
        async t =>
        {
            var filter = userId is { } u ? $"userId={u}&" : string.Empty;
            var posts = await http.GetFromJsonAsync<List<PostDto>>($"posts?{filter}_page={page}&_limit={PageSize}", t);
            return (IReadOnlyList<PostDto>)posts!;
        },
        () => [.. SeedData.Posts.Where(p => userId is null || p.UserId == userId).Skip((page - 1) * PageSize).Take(PageSize)],
        token);

    public Task<UserDto> GetUserAsync(int id, CancellationToken token) => RunAsync(
        async t => (await http.GetFromJsonAsync<UserDto>($"users/{id}", t))!,
        () => SeedData.Users.FirstOrDefault(user => user.Id == id) ?? new UserDto(id, $"User {id}", $"user{id}", $"user{id}@example.com"),
        token);

    public Task<TodoDto> CreateTodoAsync(NewTodo todo, CancellationToken token) => RunAsync(
        async t =>
        {
            var response = await http.PostAsJsonAsync("todos", todo, t);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<TodoDto>(t))!;
        },
        () => new TodoDto(201, todo.UserId, todo.Title, todo.Completed),
        token);

    public Task<TodoDto> UpdateTodoAsync(TodoDto todo, CancellationToken token) => RunAsync(
        async t =>
        {
            var response = await http.PutAsJsonAsync($"todos/{todo.Id}", todo, t);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<TodoDto>(t))!;
        },
        () => todo,
        token);

    public Task<bool> DeleteTodoAsync(int id, CancellationToken token) => RunAsync(
        async t =>
        {
            var response = await http.DeleteAsync($"todos/{id}", t);
            response.EnsureSuccessStatusCode();
            return true;
        },
        () => true,
        token);

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, Func<T> fallback, CancellationToken token)
    {
        await latency.DelayAsync(token);
        failure.ThrowIfArmed();

        try
        {
            var result = await call(token);
            SetOffline(false);
            return result;
        }
        catch (HttpRequestException)
        {
            // Network or CORS failure — the docs site must not go dark with the demo API.
            SetOffline(true);
            return fallback();
        }
    }

    private void SetOffline(bool offline)
    {
        if (IsOffline == offline) return;

        IsOffline = offline;
        OfflineChanged?.Invoke();
    }
}
