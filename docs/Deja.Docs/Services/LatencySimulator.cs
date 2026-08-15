namespace Deja.Docs.Services;

/// <summary>
/// Optional artificial delay applied before every demo request. JSONPlaceholder responds fast
/// enough that loading states flash by invisibly; the slider in the demo controls makes
/// <c>IsLoading</c> / <c>IsReFetching</c> observable.
/// </summary>
public sealed class LatencySimulator
{
    public int DelayMs { get; set; } = 600;

    public Task DelayAsync(CancellationToken token)
        => DelayMs > 0 ? Task.Delay(DelayMs, token) : Task.CompletedTask;
}
