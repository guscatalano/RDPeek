using Dvc.Diag.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// Per-DVC counters from the Windows "Remote Desktop Virtual Channel" counter set —
/// one instance per open channel, with byte totals in both directions, RTT and bandwidth.
///
/// This is the preferred source: it needs no elevation, covers both directions, and is
/// maintained by Windows. It only exists on builds that ship the counter set; where it
/// is missing, <see cref="DvcCounters"/> falls back to <see cref="DvcTrafficWatcher"/>
/// (the ETW route).
///
/// The set is registered by rdpcorets.dll, so it only has instances on the machine
/// hosting the session — see <see cref="Pdh"/> for the read mechanics.
/// </summary>
internal static class DvcPerfCounters
{
    public const string ObjectName = "Remote Desktop Virtual Channel";

    private const int Sent = 0, Received = 1, Rtt = 2, Bandwidth = 3, Open = 4;

    private static readonly string[] CounterNames =
    {
        "Total Bytes Sent To Client",
        "Total Bytes Received From Client",
        "RTT (ms)",
        "Bandwidth (kbps)",
        "Currently Open",
    };

    private static readonly object Gate = new();
    private static readonly RateTracker SendRates = new();
    private static readonly RateTracker RecvRates = new();

    public static bool ObjectPresent => Pdh.ObjectPresent(ObjectName);

    /// <summary>
    /// One sample across every open channel. Channels are absent (with a note) rather
    /// than zeroed when nothing is open.
    /// </summary>
    public static CounterSample Collect()
    {
        var sample = new CounterSample
        {
            SampledUtcTicks = DateTime.UtcNow.Ticks,
            Source = "perfmon",
        };

        if (!ObjectPresent)
        {
            sample.Note = $"The '{ObjectName}' counter set is not present on this Windows build.";
            return sample;
        }

        lock (Gate)
        {
            var byInstance = Pdh.ReadInstances(ObjectName, CounterNames);
            foreach (var (instance, v) in byInstance.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                ulong sent = ToUInt64(v[Sent]), received = ToUInt64(v[Received]);
                sample.Channels.Add(new CounterSample.Types.ChannelCounters
                {
                    Name = instance,
                    BytesSent = sent,
                    BytesReceived = received,
                    SendRateBps = SendRates.Sample(instance, (long)sent, sample.SampledUtcTicks),
                    RecvRateBps = RecvRates.Sample(instance, (long)received, sample.SampledUtcTicks),
                    RttMs = Math.Max(v[Rtt], 0),
                    BandwidthKbps = Math.Max(v[Bandwidth], 0),
                    CurrentlyOpen = (uint)Math.Max(v[Open], 0),
                });
            }

            var live = new HashSet<string>(byInstance.Keys, StringComparer.OrdinalIgnoreCase);
            SendRates.Retain(live);
            RecvRates.Retain(live);
        }

        if (sample.Channels.Count == 0)
            sample.Note = "No RDP virtual channels are open on this host right now.";

        return sample;
    }

    /// <summary>Channel instances currently exposed by the counter set.</summary>
    public static IReadOnlyList<string> InstanceNames()
        => Pdh.ReadInstances(ObjectName, CounterNames).Keys
              .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    private static ulong ToUInt64(double value) => double.IsFinite(value) && value > 0 ? (ulong)value : 0UL;
}
