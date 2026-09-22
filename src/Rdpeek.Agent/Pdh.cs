using System.Runtime.InteropServices;

namespace Rdpeek.Agent;

/// <summary>
/// Minimal PDH wrapper for the counter sets that are instanced per channel or per
/// session ("Remote Desktop Virtual Channel", "RemoteFX Network", …).
///
/// A query is built and torn down per read on purpose: PDH expands a wildcard instance
/// path when the counter is <em>added</em>, so a long-lived query never sees instances
/// that appeared later — and with RDP, instances come and go with channels and sessions.
///
/// Only cumulative and instantaneous counters are read, never the "/sec" ones: those
/// need two collects a second apart, and callers run on the agent's channel-read thread
/// where a sleep stalls everything else. Derive rates with <see cref="RateTracker"/>.
/// </summary>
internal static class Pdh
{
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PERF_DETAIL_WIZARD = 400;

    private static readonly Dictionary<string, bool> PresenceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this Windows build registers the counter object at all — distinct from
    /// "it exists but has no live instances". Cached: a build does not grow counter sets
    /// while the agent runs.
    /// </summary>
    public static bool ObjectPresent(string objectName)
    {
        lock (PresenceCache)
        {
            if (PresenceCache.TryGetValue(objectName, out bool known)) return known;

            uint counterLen = 0, instanceLen = 0;
            uint rc = PdhEnumObjectItemsW(null, null, objectName,
                IntPtr.Zero, ref counterLen, IntPtr.Zero, ref instanceLen,
                PERF_DETAIL_WIZARD, 0);

            // Querying with no buffer answers "more data" when the object exists;
            // PDH_CSTATUS_NO_OBJECT means this build doesn't have the counter set.
            bool present = rc is PDH_MORE_DATA or 0;
            PresenceCache[objectName] = present;
            Logger.Log(present
                ? $"pdh: counter set '{objectName}' is present."
                : $"pdh: counter set '{objectName}' not present (0x{rc:X8}).");
            return present;
        }
    }

    /// <summary>
    /// Read every live instance of <paramref name="objectName"/>. The returned array is
    /// indexed like <paramref name="counterNames"/>; counters missing on this build are
    /// left at 0. Empty result = the set exists but nothing is open.
    /// </summary>
    public static Dictionary<string, double[]> ReadInstances(string objectName, string[] counterNames)
    {
        var byInstance = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        if (!ObjectPresent(objectName)) return byInstance;
        if (PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query) != 0) return byInstance;

        try
        {
            var handles = new IntPtr[counterNames.Length];
            bool anyAdded = false;
            for (int i = 0; i < counterNames.Length; i++)
            {
                // Wildcard instance: one handle covers every instance open at add time.
                // Adds fail when nothing matches, which is the "nothing open" case.
                if (PdhAddEnglishCounterW(query, $@"\{objectName}(*)\{counterNames[i]}", IntPtr.Zero, out handles[i]) != 0)
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
                        byInstance[instance] = values = new double[counterNames.Length];
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

    /// <summary>
    /// A query held open across reads, for counter sets containing rate or average
    /// types ("…/sec", "… Rate", "Average …"). Those are computed from the delta
    /// between two collects, so a one-shot <see cref="ReadInstances"/> reads them as
    /// zero — silently, which is the dangerous part.
    ///
    /// Holding the query costs the other half of the trade: PDH expands wildcard
    /// instances when a counter is added, so this never sees instances that appear
    /// later. Callers must dispose and reopen when a read comes back empty. That is
    /// tolerable for session-scoped sets (RemoteFX) and wrong for churning ones
    /// (per-channel), which is why both modes exist.
    /// </summary>
    public sealed class Query : IDisposable
    {
        private readonly string[] _counterNames;
        private readonly IntPtr[] _handles;
        private IntPtr _query;

        private Query(IntPtr query, IntPtr[] handles, string[] counterNames)
            => (_query, _handles, _counterNames) = (query, handles, counterNames);

        /// <summary>Open and take a baseline sample. Null when no instance matched.</summary>
        public static Query? Open(string objectName, string[] counterNames)
        {
            if (!ObjectPresent(objectName)) return null;
            if (PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query) != 0) return null;

            var handles = new IntPtr[counterNames.Length];
            bool anyAdded = false;
            for (int i = 0; i < counterNames.Length; i++)
            {
                if (PdhAddEnglishCounterW(query, $@"\{objectName}(*)\{counterNames[i]}", IntPtr.Zero, out handles[i]) != 0)
                    handles[i] = IntPtr.Zero;
                else
                    anyAdded = true;
            }

            if (!anyAdded)
            {
                PdhCloseQuery(query);
                return null;
            }

            // Baseline, so the first Read() already has a previous sample to diff.
            PdhCollectQueryData(query);
            return new Query(query, handles, counterNames);
        }

        public Dictionary<string, double[]> Read()
        {
            var byInstance = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return byInstance;

            for (int i = 0; i < _handles.Length; i++)
            {
                if (_handles[i] == IntPtr.Zero) continue;
                foreach (var (instance, value) in ReadArray(_handles[i]))
                {
                    if (!byInstance.TryGetValue(instance, out var values))
                        byInstance[instance] = values = new double[_counterNames.Length];
                    values[i] = value;
                }
            }

            return byInstance;
        }

        public void Dispose()
        {
            if (_query == IntPtr.Zero) return;
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    /// <summary>Read one wildcard counter as (instance, value) pairs.</summary>
    private static IEnumerable<(string Instance, double Value)> ReadArray(IntPtr counter)
    {
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA || size == 0)
            yield break;

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
