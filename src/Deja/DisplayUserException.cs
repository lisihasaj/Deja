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
