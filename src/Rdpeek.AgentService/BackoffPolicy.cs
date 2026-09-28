namespace Rdpeek.AgentService;

/// <summary>
/// Pure (no P/Invoke, no clock) restart/backoff decision logic, split out so it can be
/// unit-tested without a session or a real process.
///
/// The rule: an agent that exits while its session is still active gets relaunched, but
/// with an increasing delay so a crash-looping agent (or a bad build) can't spin the CPU.
/// Fast, healthy exits reset the streak; only *rapid* repeated exits escalate the delay.
/// </summary>
internal sealed class BackoffPolicy
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly TimeSpan _healthyRunResetsStreak;

    public BackoffPolicy(
        TimeSpan? initial = null,
        TimeSpan? max = null,
        TimeSpan? healthyRunResetsStreak = null)
    {
        _initial = initial ?? TimeSpan.FromSeconds(2);
        _max = max ?? TimeSpan.FromSeconds(60);
        // If the agent stayed up at least this long before dying, we treat the exit as a
        // one-off (reconnect churn, transient error) rather than a crash loop.
        _healthyRunResetsStreak = healthyRunResetsStreak ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Given how many times in a row the agent has crashed quickly and how long it ran
    /// this time, return the next consecutive-failure count and the delay before relaunch.
    /// </summary>
    public (int nextFailureCount, TimeSpan delay) NextDelay(int consecutiveFailures, TimeSpan lastRunDuration)
    {
        // A run that lasted long enough is considered healthy: reset the streak and
        // relaunch immediately.
        if (lastRunDuration >= _healthyRunResetsStreak)
            return (0, TimeSpan.Zero);

        int next = consecutiveFailures + 1;

        // Exponential backoff: initial * 2^(next-1), capped at _max.
        double seconds = _initial.TotalSeconds * Math.Pow(2, next - 1);
        var delay = TimeSpan.FromSeconds(Math.Min(seconds, _max.TotalSeconds));
        return (next, delay);
    }
}
