namespace Deja.Docs.Services;

public sealed record TodoDto(int Id, int UserId, string Title, bool Completed);

public sealed record PostDto(int Id, int UserId, string Title, string Body);

public sealed record UserDto(int Id, string Name, string Username, string Email);

public sealed record NewTodo(int UserId, string Title, bool Completed);
