namespace Rdpeek.Agent;

/// <summary>
/// Turns cumulative per-key totals into a per-second rate, remembering the previous
/// total and when it was taken.
///
/// Both counter sources report cumulative bytes, and deriving the rate here means
/// neither has to hold a sampling query open (or sleep a second) just to let Windows
/// compute a "/sec" counter for us.
/// </summary>
internal sealed class RateTracker
{
    private readonly Dictionary<string, (long Total, long Ticks)> _last = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rate in units/second since this key was last sampled. Returns 0 the first time a
    /// key is seen, and after a counter reset (channel reopened, totals rolled back).
    /// </summary>
    public double Sample(string key, long total, long nowTicks)
    {
        double rate = 0;
        if (_last.TryGetValue(key, out var prev) && total >= prev.Total && nowTicks > prev.Ticks)
        {
            double seconds = (nowTicks - prev.Ticks) / (double)TimeSpan.TicksPerSecond;
            if (seconds > 0.001) rate = (total - prev.Total) / seconds;
        }

        _last[key] = (total, nowTicks);
        return rate;
    }

    /// <summary>Drop keys that no longer exist, so a churning channel set can't grow this forever.</summary>
    public void Retain(IReadOnlyCollection<string> keys)
    {
        foreach (var stale in _last.Keys.Where(k => !keys.Contains(k)).ToList())
            _last.Remove(stale);
    }
}
