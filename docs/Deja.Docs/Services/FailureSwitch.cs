namespace Deja.Docs.Services;

/// <summary>
/// Arms a one-shot failure for the next demo request, so the error-handling docs have a working
/// demo. <see cref="FailureKind.DisplayUser"/> throws a <see cref="DisplayUserException"/>, the
/// kind whose message is intended for end users.
/// </summary>
public sealed class FailureSwitch
{
    public enum FailureKind { None, Generic, DisplayUser }

    public FailureKind Armed { get; private set; }

    public event Action? Changed;

    public void Arm(FailureKind kind)
    {
        Armed = kind;
        Changed?.Invoke();
    }

    public void ThrowIfArmed()
    {
        var kind = Armed;
        if (kind == FailureKind.None) return;

        Armed = FailureKind.None;
        Changed?.Invoke();

        throw kind switch
        {
            FailureKind.DisplayUser => new DisplayUserException(
                "We couldn't load your data. Please try again.",
                "Simulated failure armed from the demo controls."),
            _ => new HttpRequestException("Simulated network failure (armed from the demo controls)."),
        };
    }
}
