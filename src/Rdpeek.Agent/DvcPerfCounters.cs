using System.Runtime.InteropServices;
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
/// Only cumulative counters are read, in a single collect, with rates derived by
/// <see cref="RateTracker"/>. Reading the "/sec" counters instead would mean two
/// collects a second apart — and the agent serves this on its channel-read thread, so a
/// sleep there stalls everything else. The query is also rebuilt per snapshot on
/// purpose: PDH expands a wildcard instance path when the counter is added, so a
/// long-lived query would never see channels that opened after it.
/// </summary>
internal static class DvcPerfCounters
{
    public const string ObjectName = "Remote Desktop Virtual Channel";

    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PERF_DETAIL_WIZARD = 400;

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

    private static bool? _objectPresent;

    /// <summary>
    /// True when this Windows build exposes the counter set at all (cached — a build
    /// does not grow the counter set while the agent runs).
    /// </summary>
    public static bool ObjectPresent
    {
        get
        {
            if (_objectPresent is { } known) return known;

            uint counterLen = 0, instanceLen = 0;
            uint rc = PdhEnumObjectItemsW(null, null, ObjectName,
                IntPtr.Zero, ref counterLen, IntPtr.Zero, ref instanceLen,
                PERF_DETAIL_WIZARD, 0);

            // Querying with no buffer answers "more data" when the object exists;
            // PDH_CSTATUS_NO_OBJECT means this build doesn't have the counter set.
            bool present = rc is PDH_MORE_DATA or 0;
            _objectPresent = present;
            Logger.Log(present
                ? $"dvc counters: using the '{ObjectName}' counter set."
                : $"dvc counters: '{ObjectName}' not present (pdh 0x{rc:X8}) — falling back to ETW.");
            return present;
        }
    }

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
            var byInstance = ReadInstances();
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
    {
        lock (Gate)
            return ReadInstances().Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>instance name -> values indexed like <see cref="CounterNames"/>.</summary>
    private static Dictionary<string, double[]> ReadInstances()
    {
        var byInstance = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        if (PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query) != 0) return byInstance;

        try
        {
            var handles = new IntPtr[CounterNames.Length];
            bool anyAdded = false;
            for (int i = 0; i < CounterNames.Length; i++)
            {
                // Wildcard instance: one handle covers every channel open at add time.
                // An add fails when nothing matches, which is the "no channels" case.
                if (PdhAddEnglishCounterW(query, $@"\{ObjectName}(*)\{CounterNames[i]}", IntPtr.Zero, out handles[i]) != 0)
                    handles[i] = IntPtr.Zero;
                else
                    anyAdded = true;
            }

            if (!anyAdded || PdhCollectQueryData(query) != 0) return byInstance;

            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i] == IntPtr.Zero) continue;
                foreach (var (instance, value) in ReadArray(handles[i]))
                {
                    if (!byInstance.TryGetValue(instance, out var values))
                        byInstance[instance] = values = new double[CounterNames.Length];
                    values[i] = value;
                }
            }
        }
        finally
        {
            PdhCloseQuery(query);
        }

        return byInstance;
    }

    private static ulong ToUInt64(double value) => double.IsFinite(value) && value > 0 ? (ulong)value : 0UL;

    /// <summary>Read one wildcard counter as (instance, value) pairs.</summary>
    private static IEnumerable<(string Instance, double Value)> ReadArray(IntPtr counter)
    {
        uint size = 0;
        uint rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
        if (rc != PDH_MORE_DATA || size == 0) yield break;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out uint count, buffer) != 0)
                yield break;

            int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * stride);
                if (item.CStatus != 0) continue;   // this instance's value isn't valid
                string? name = item.szName == IntPtr.Zero ? null : Marshal.PtrToStringUni(item.szName);
                if (!string.IsNullOrEmpty(name)) yield return (name, item.doubleValue);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR szName; PDH_FMT_COUNTERVALUE FmtValue; }
    // The value union is 8-aligned, so on x64 the double lands at offset 16.
    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        [FieldOffset(0)] public IntPtr szName;
        [FieldOffset(8)] public uint CStatus;
        [FieldOffset(16)] public double doubleValue;
    }

    [DllImport("pdh.dll")]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhEnumObjectItemsW(
        string? dataSource, string? machineName, string objectName,
        IntPtr counterList, ref uint counterListLength,
        IntPtr instanceList, ref uint instanceListLength,
        uint detailLevel, uint flags);
}
