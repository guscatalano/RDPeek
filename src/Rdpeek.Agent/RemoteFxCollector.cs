using Dvc.Diag.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// RDP link quality from the "RemoteFX Network" and "RemoteFX Graphics" counter sets:
/// RTT, bandwidth, loss and retransmits for the connection, plus how many frames the
/// host is dropping and whose fault it is.
///
/// Same story as the DVC channel counters — rdpcorets.dll registers these, so they only
/// have instances on the session host. That is why they are collected by the agent and
/// relayed, rather than read by the companion: there is no client-side equivalent, and
/// mstsc publishes no perfmon counters at all.
/// </summary>
internal static class RemoteFxCollector
{
    private const string NetworkObject = "RemoteFX Network";
    private const string GraphicsObject = "RemoteFX Graphics";

    // Curated rather than exhaustive — these are the ones worth a dashboard row.
    //
    // Units are deliberately left blank. Windows does not document them consistently
    // for this set (RTT in ms vs µs, bandwidth in bps vs kbps vary by counter), and a
    // confidently wrong unit is worse than none. The counter's own name is shown, so
    // whatever Windows means is what the operator reads.
    private static readonly string[] NetworkCounters =
    {
        "Current TCP RTT",
        "Base TCP RTT",
        "Current UDP RTT",
        "Base UDP RTT",
        "Current TCP Bandwidth",
        "Current UDP Bandwidth",
        "Total Sent Rate",
        "Total Received Rate",
        "Loss Rate",
        "Retransmission Rate",
        "FEC Rate",
    };

    private static readonly string[] GraphicsCounters =
    {
        "Frame Quality",
        "Average Encoding Time",
        "Input Frames/Second",
        "Output Frames/Second",
        "Frames Skipped/Second - Insufficient Server Resources",
        "Frames Skipped/Second - Insufficient Network Resources",
        "Frames Skipped/Second - Insufficient Client Resources",
        "Graphics Compression ratio",
    };

    // Held open across polls: most of the counters above are rate or average types,
    // which Windows computes from the delta between two collects. A fresh query per
    // read would report every one of them as zero.
    private static readonly object Gate = new();
    private static Pdh.Query? _network;
    private static Pdh.Query? _graphics;

    /// <summary>Append link + graphics counters to a snapshot. No-op off the session host.</summary>
    public static void AddTo(PerfSnapshot snapshot)
    {
        lock (Gate)
        {
            Add(snapshot, NetworkObject, NetworkCounters, "link", ref _network);
            Add(snapshot, GraphicsObject, GraphicsCounters, "graphics", ref _graphics);
        }
    }

    private static void Add(PerfSnapshot snapshot, string objectName, string[] counters, string group, ref Pdh.Query? query)
    {
        query ??= Pdh.Query.Open(objectName, counters);
        if (query is null) return;

        var instances = query.Read();
        if (instances.Count == 0)
        {
            // The session went away, or none had started when the query was opened.
            // Reopen next time so newly-created instances get picked up.
            query.Dispose();
            query = null;
            return;
        }

        foreach (var (instance, values) in instances.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            for (int i = 0; i < counters.Length; i++)
            {
                if (!double.IsFinite(values[i])) continue;
                snapshot.Counters.Add(new PerfSnapshot.Types.Counter
                {
                    Name = counters[i],
                    Value = Math.Round(values[i], 2),
                    Group = group,
                    Instance = instance,
                });
            }
        }
    }
}
