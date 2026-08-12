namespace Deja.Tests;

/// <summary>
/// A manually advanced clock for staleness and eviction tests, so nothing sleeps. The eviction
/// sweep is driven directly via the internal <c>DejaClient.Sweep()</c> rather than through timer
/// ticks, so only <see cref="TimeProvider.GetUtcNow"/> needs overriding.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
