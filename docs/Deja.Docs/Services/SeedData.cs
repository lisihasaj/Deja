namespace Deja.Docs.Services;

/// <summary>
/// Embedded fallback data served when JSONPlaceholder is unreachable, so the live demos keep
/// working offline. Mirrors the shape (and the first rows) of the real API.
/// </summary>
public static class SeedData
{
    public static readonly IReadOnlyList<TodoDto> Todos =
    [
        new(1, 1, "delectus aut autem", false),
        new(2, 1, "quis ut nam facilis et officia qui", false),
        new(3, 1, "fugiat veniam minus", false),
        new(4, 1, "et porro tempora", true),
        new(5, 1, "laboriosam mollitia et enim quasi adipisci quia provident illum", false),
        new(6, 1, "qui ullam ratione quibusdam voluptatem quia omnis", false),
        new(7, 1, "illo expedita consequatur quia in", false),
        new(8, 1, "quo adipisci enim quam ut ab", true),
        new(9, 1, "molestiae perspiciatis ipsa", false),
        new(10, 1, "illo est ratione doloremque quia maiores aut", true),
        new(11, 1, "vero rerum temporibus dolor", true),
        new(12, 1, "ipsa repellendus fugit nisi", true),
    ];

    public static readonly IReadOnlyList<PostDto> Posts =
    [
        new(1, 1, "sunt aut facere repellat provident", "quia et suscipit\nsuscipit recusandae"),
        new(2, 1, "qui est esse", "est rerum tempore vitae\nsequi sint nihil"),
        new(3, 1, "ea molestias quasi exercitationem", "et iusto sed quo iure"),
        new(4, 2, "eum et est occaecati", "ullam et saepe reiciendis voluptatem adipisci"),
        new(5, 2, "nesciunt quas odio", "repudiandae veniam quaerat sunt sed"),
        new(6, 2, "dolorem eum magni eos aperiam quia", "ut aspernatur corporis harum nihil"),
        new(7, 3, "magnam facilis autem", "dolore placeat quibusdam ea quo vitae"),
        new(8, 3, "dolorem dolore est ipsam", "dignissimos aperiam dolorem qui eum"),
    ];

    public static readonly IReadOnlyList<UserDto> Users =
    [
        new(1, "Leanne Graham", "Bret", "Sincere@april.biz"),
        new(2, "Ervin Howell", "Antonette", "Shanna@melissa.tv"),
        new(3, "Clementine Bauch", "Samantha", "Nathan@yesenia.net"),
    ];
}
