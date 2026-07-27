using Dvc.Diag.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// Picks where per-DVC traffic numbers come from and hands the client one shape of
/// answer regardless.
///
/// Both sources are server-side — they measure what the session host sends to and
/// receives from the client, so they only produce numbers when the agent runs inside an
/// RDP session. There is no client-side equivalent: mstsc exposes no such counters.
///
///   1. <see cref="DvcPerfCounters"/> — the "Remote Desktop Virtual Channel" counter set.
///      Preferred: no elevation, both directions, RTT and bandwidth, maintained by Windows.
///   2. <see cref="DvcTrafficWatcher"/> — ETW write-flush events, ported from
///      RDP_DVC_Watcher. Used only where the counter set is absent; needs Administrator
///      and sees the send direction only.
/// </summary>
internal static class DvcCounters
{
    /// <summary>Which source will answer, for logs and the capability handshake.</summary>
    public static string SourceName => DvcPerfCounters.ObjectPresent ? "perfmon" : "etw";

    /// <summary>
    /// True when some source can produce channel traffic here. Probing the ETW route
    /// starts its session, so that only happens when the counter set is missing.
    /// </summary>
    public static bool Available
        => DvcPerfCounters.ObjectPresent || DvcTrafficWatcher.Instance.EnsureStarted();

    public static CounterSample Snapshot()
        => DvcPerfCounters.ObjectPresent ? DvcPerfCounters.Collect() : DvcTrafficWatcher.Instance.Snapshot();

    /// <summary>The channels the agent can currently see carrying traffic.</summary>
    public static ChannelRoster Roster()
    {
        if (!DvcPerfCounters.ObjectPresent) return DvcTrafficWatcher.Instance.Roster();

        var roster = new ChannelRoster();
        foreach (var name in DvcPerfCounters.InstanceNames())
            roster.Entries.Add(new ChannelRoster.Types.Entry { Name = name, Active = true });
        return roster;
    }

    public static void Stop() => DvcTrafficWatcher.Instance.Stop();
}
