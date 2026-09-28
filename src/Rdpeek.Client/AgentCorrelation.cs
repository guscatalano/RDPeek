using System;
using System.Collections.Generic;
using System.Linq;

namespace Rdpeek.Client;

/// <summary>
/// Joins open RDP windows to connected agents. Pure and transport-free so it's unit-testable (this is
/// the logic that repeatedly regressed for multi-connection / gateway / Cloud PC setups). Three passes,
/// each claiming an agent so two windows can't share one:
///   0. exact client-pid match — the plugin reports the RDP client's pid; reliable even through a gateway
///   1. host-name match — normalized short name (DNS domain dropped, case-insensitive)
///   2. order-pair the rest — a stable, marked-inferred fallback when names can't be matched
/// </summary>
public static class AgentCorrelation
{
    /// <summary>The join result per window: the assigned agent (or null) and a short status label.</summary>
    public static Dictionary<IntPtr, (BrokerServer.AgentState? State, string Text)> Assign(
        IReadOnlyList<RdpWindow> windows, IReadOnlyList<BrokerServer.AgentState> states)
    {
        var map = new Dictionary<IntPtr, (BrokerServer.AgentState?, string)>();
        var connected = states.Where(s => s.Status == "connected").ToList();
        var claimed = new HashSet<BrokerServer.AgentState>();

        // Pass 0: exact client-pid match (certain, gateway/Cloud PC safe).
        foreach (var w in windows)
        {
            var m = connected.FirstOrDefault(s => !claimed.Contains(s) && s.ClientPid != 0 && s.ClientPid == w.Pid);
            if (m is not null) { claimed.Add(m); map[w.Hwnd] = (m, $"✓ {HostLabel(m)}"); }
        }

        // Pass 1: confident host match.
        foreach (var w in windows)
        {
            if (map.ContainsKey(w.Hwnd)) continue;
            var m = connected.FirstOrDefault(s => !claimed.Contains(s) && HostsMatch(s.Host, w.Host));
            if (m is not null) { claimed.Add(m); map[w.Hwnd] = (m, $"✓ {m.Host}"); }
        }

        // Pass 2: order-pair leftovers (inferred, "≈") — stable by hwnd/seq so rows don't swap each tick.
        var freeWins = windows.Where(w => !map.ContainsKey(w.Hwnd)).OrderBy(w => w.Hwnd.ToInt64()).ToList();
        var freeAgents = connected.Where(s => !claimed.Contains(s)).OrderBy(s => s.Seq).ThenBy(s => s.Pid).ToList();
        for (int i = 0; i < freeWins.Count && i < freeAgents.Count; i++)
        {
            var a = freeAgents[i]; claimed.Add(a);
            map[freeWins[i].Hwnd] = (a, $"≈ {HostLabel(a)}");
        }

        // Remainder: no agent.
        int awaiting = states.Count(s => s.Status == "listening");
        foreach (var w in windows)
            if (!map.ContainsKey(w.Hwnd))
                map[w.Hwnd] = (null, (connected.Count == 0 && awaiting > 0) ? "⚠ no agent" : "—");

        return map;
    }

    private static string HostLabel(BrokerServer.AgentState s) => string.IsNullOrEmpty(s.Host) ? "connected" : s.Host;

    /// <summary>Do two host strings refer to the same machine? Case-insensitive on the short name (DNS
    /// domain dropped), so "SERVER01", "server01" and "server01.corp.local" all match.</summary>
    public static bool HostsMatch(string? a, string? b)
    {
        static string Short(string? h)
        {
            h = (h ?? "").Trim().ToLowerInvariant();
            int dot = h.IndexOf('.');
            return dot > 0 ? h[..dot] : h;
        }
        var na = Short(a); var nb = Short(b);
        return na.Length > 0 && nb.Length > 0 && na == nb;
    }
}
