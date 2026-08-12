namespace Deja;

/// <summary>
/// An exception whose <see cref="DisplayMessage"/> is safe — and intended — to be shown to the
/// end user. <see cref="Query{T}"/> and <see cref="Mutation{T}"/> route it to the dedicated
/// <c>OnDisplayUserError</c> callbacks in addition to the general error callbacks.
/// </summary>
public class DisplayUserException : Exception
{
    /// <summary>The user-facing message.</summary>
    public string DisplayMessage { get; set; }

    /// <summary>Creates the exception with an empty display message.</summary>
    public DisplayUserException()
    {
        DisplayMessage = string.Empty;
    }

    /// <summary>Creates the exception; <paramref name="message"/> doubles as the display message.</summary>
    public DisplayUserException(string message)
            : base(message)
    {
        DisplayMessage = message;
    }

    /// <summary>Creates the exception; <paramref name="message"/> doubles as the display message.</summary>
    public DisplayUserException(string message, Exception ex)
            : base(message, ex)
    {
        DisplayMessage = message;
    }

    /// <summary>
    /// Creates the exception with a user-facing <paramref name="message"/> and a separate
    /// <paramref name="internalMessage"/> used as the <see cref="Exception.Message"/>.
    /// </summary>
    public DisplayUserException(string message, string internalMessage)
            : base(internalMessage)
    {
        DisplayMessage = message;
    }

    /// <summary>
    /// Creates the exception with a user-facing <paramref name="message"/>, a separate
    /// <paramref name="internalMessage"/> used as the <see cref="Exception.Message"/>, and an
    /// inner exception.
    /// </summary>
    public DisplayUserException(string message, string internalMessage, Exception ex)
            : base(internalMessage, ex)
    {
        DisplayMessage = message;
    }
}

/// <summary>
/// The single-retry policy for <c>HttpClient.Timeout</c> expiry, shared by the uncached
/// <see cref="Query{T}"/> path and cache-entry fetches.
/// </summary>
internal static class TimeoutRetry
{
    internal static async Task<T> FetchAsync<T>(Func<CancellationToken, Task<T>> fetch, CancellationToken token)
    {
        try
        {
            return await fetch(token);
        }
        catch (TaskCanceledException tce) when (tce.InnerException is TimeoutException && !token.IsCancellationRequested)
        {
            // HttpClient.Timeout expiry is the only TaskCanceledException carrying an inner
            // TimeoutException, so this cannot catch a caller's own cancellation. The typical
            // cause is a browser freezing an inactive tab mid-request until the timeout budget
            // elapsed. Queries are idempotent reads, so retry once — on resume it completes in
            // milliseconds. A second timeout falls through to the normal error path.
            return await fetch(token);
        }
    }
}
