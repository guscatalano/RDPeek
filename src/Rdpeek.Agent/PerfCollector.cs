using System.Runtime.InteropServices;
using Dvc.Diag.Protocol;

namespace Rdpeek.Agent;

/// <summary>A curated set of host performance counters via PDH (perfmon-style).</summary>
internal static class PerfCollector
{
    [DllImport("pdh.dll")]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private const uint PDH_FMT_DOUBLE = 0x00000200;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    private sealed record Def(string Path, string Name, string Unit, double Scale = 1.0);

    private static readonly Def[] Counters =
    {
        new(@"\Processor(_Total)\% Processor Time", "CPU", "%"),
        new(@"\Memory\% Committed Bytes In Use", "Memory committed", "%"),
        new(@"\Memory\Available MBytes", "Memory available", "MB"),
        new(@"\PhysicalDisk(_Total)\% Disk Time", "Disk busy", "%"),
        new(@"\PhysicalDisk(_Total)\Disk Bytes/sec", "Disk I/O", "MB/s", 1.0 / 1048576),
        new(@"\System\Processor Queue Length", "CPU queue", ""),
        new(@"\System\Threads", "Threads", ""),
    };

    public static PerfSnapshot Collect()
    {
        var snap = new PerfSnapshot();
        if (PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query) != 0) return snap;
        try
        {
            var handles = new IntPtr[Counters.Length];
            for (int i = 0; i < Counters.Length; i++)
                PdhAddEnglishCounterW(query, Counters[i].Path, IntPtr.Zero, out handles[i]);

            PdhCollectQueryData(query);
            Thread.Sleep(1000);              // rate counters need a second sample
            PdhCollectQueryData(query);

            for (int i = 0; i < Counters.Length; i++)
            {
                if (handles[i] == IntPtr.Zero) continue;
                if (PdhGetFormattedCounterValue(handles[i], PDH_FMT_DOUBLE, out _, out var v) == 0)
                    snap.Counters.Add(new PerfSnapshot.Types.Counter
                    {
                        Name = Counters[i].Name,
                        Value = Math.Round(v.doubleValue * Counters[i].Scale, 2),
                        Unit = Counters[i].Unit,
                    });
            }
        }
        finally
        {
            PdhCloseQuery(query);
        }
        return snap;
    }
}
