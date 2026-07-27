using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Rdpeek.Doctor;

/// <summary>
/// Opt-in client-side counterpart to the agent's DVC traffic counters: listens to
/// mstsc's own ETW providers and reports any per-channel byte counts they expose.
///
/// Why this is a probe and not a collector. Everything that measures DVC traffic
/// reliably lives on the server:
///   * The "Remote Desktop Virtual Channel" counter set is registered by rdpcorets.dll
///     (server-side RDP core), so it has no instances on a machine that is only running
///     mstsc.
///   * The provider RDP_DVC_Watcher used (8375996d-…, TraceLogging name
///     "Microsoft.Windows.RemoteDesktop.ServerBase") is compiled into rdpserverbase.dll
///     only — which is exactly why that tool "does not work from the client".
/// The client providers below do exist, but whether a given Windows build emits a
/// channel name and a byte count from them is a per-build question. So this command
/// samples them and prints what is actually there, rather than pretending to know.
///
/// Needs Administrator (any real-time ETW session does).
/// </summary>
internal static class ClientDvcProbe
{
    /// <summary>
    /// Client-side RDP providers. GUIDs were read out of the TraceLogging provider
    /// metadata in the shipping binaries — most are the hash of the provider name, but
    /// several (ClientCore, Base, Legacy) override it, so they cannot be re-derived.
    /// </summary>
    private static readonly (string Name, string Guid, string Module)[] Providers =
    {
        ("Microsoft.Windows.RemoteDesktop.ClientCore",         "080656c2-c24f-4660-8f5a-ce83656b0e7c", "mstscax.dll"),
        ("Microsoft.Windows.RemoteDesktop.Base",               "5795aab9-b0e3-419e-b0ef-7aef943cffa8", "rdpbase.dll"),
        ("Microsoft.Windows.RemoteDesktop.Legacy",             "3ef15adf-1300-44a1-b85c-2a83549f5b9e", "RdpRelayTransport.dll"),
        ("Microsoft.Windows.RDP.NamedPipe",                    "eb6594d8-6fad-53f7-350e-f4e4c531f68c", "mstscax.dll"),
        ("Microsoft.Windows.RemoteDesktopServices.RailPlugin", "43471865-f3ee-5dcf-bf8b-193fcbbe0f37", "mstscax.dll"),
        ("Microsoft-Windows-TerminalServices-ClientActiveXCore", "28aa95bb-d444-4719-a36f-40462168127e", "mstscax.dll"),
    };

    // Payload fields that plausibly carry a channel name / a byte count. Checked in
    // order, so the most specific name wins.
    private static readonly string[] NameFields = { "ChannelName", "Channel", "Name", "EndpointName" };
    private static readonly string[] SizeFields =
        { "Size", "cbSize", "ByteCount", "Length", "cbData", "DataLength",
          "PacketSize", "cbPacketSize", "BytesRead", "BytesWritten", "BytesToSend" };

    /// <summary>Marks a row that is transport-level, not attributable to a channel.</summary>
    private const string TransportPrefix = "(transport) ";

    private const string SessionName = "RDPeek-ClientDvcProbe";

    private static readonly ConcurrentDictionary<string, long> Totals = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> EventShapes = new(StringComparer.Ordinal);
    private static long _eventsSeen;

    public static int Run(bool discover, int seconds)
    {
        if (TraceEventSession.IsElevated() != true)
        {
            Console.Error.WriteLine("  dvcprobe needs Administrator — an ETW real-time session requires it.");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("  DVC client probe — listening to mstsc's own ETW providers");
        Console.WriteLine($"  Mode: {(discover ? "discover (what do these providers emit?)" : "count per-channel bytes")}" +
                          $" | Duration: {(seconds > 0 ? seconds + "s" : "until Ctrl+C")}");
        Console.WriteLine("  Connect or use an RDP session now so there is traffic to see.");
        Console.WriteLine("  " + new string('-', 68));

        using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        foreach (var (name, guid, module) in Providers)
        {
            try
            {
                session.EnableProvider(Guid.Parse(guid));
                Console.WriteLine($"  [ON ] {name}  ({module})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERR] {name}: {ex.Message}");
            }
        }
        Console.WriteLine();

        session.Source.Dynamic.All += discover ? Discover : Count;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; session.Dispose(); };
        if (seconds > 0)
        {
            var timer = new Thread(() => { Thread.Sleep(seconds * 1000); session.Dispose(); }) { IsBackground = true };
            timer.Start();
        }

        session.Source.Process();   // returns when the session is disposed

        Console.WriteLine();
        Console.WriteLine("  " + new string('-', 68));
        return discover ? ReportShapes() : ReportTotals();
    }

    /// <summary>Record which events exist and what their payloads are called.</summary>
    private static void Discover(TraceEvent ev)
    {
        Interlocked.Increment(ref _eventsSeen);
        string fields = ev.PayloadNames is { Length: > 0 } names ? string.Join(",", names) : "(no payload)";
        EventShapes.AddOrUpdate($"{ev.ProviderName}|{ev.EventName}|{fields}", 1, (_, n) => n + 1);
    }

    /// <summary>Sum byte counts per channel, for any event that carries both.</summary>
    private static void Count(TraceEvent ev)
    {
        Interlocked.Increment(ref _eventsSeen);
        try
        {
            if (FirstNumber(ev, SizeFields) is not { } bytes || bytes <= 0) return;

            // Client events that name a channel are what we're after. Most only carry a
            // transport byte count, which is still worth totalling — just not per-DVC,
            // so it is bucketed by event and labelled as such.
            string key = FirstString(ev, NameFields) is { Length: > 0 } channel
                ? channel
                : TransportPrefix + ev.EventName;

            Totals.AddOrUpdate(key, bytes, (_, total) => total + bytes);
        }
        catch
        {
            // One odd event must not stop the probe.
        }
    }

    private static string? FirstString(TraceEvent ev, string[] candidates)
    {
        foreach (var field in candidates)
            if (ev.PayloadByName(field) is string s && s.Length > 0) return s;
        return null;
    }

    private static long? FirstNumber(TraceEvent ev, string[] candidates)
    {
        foreach (var field in candidates)
        {
            switch (ev.PayloadByName(field))
            {
                case int i: return i;
                case uint u: return u;
                case long l: return l;
                case ulong ul when ul <= long.MaxValue: return (long)ul;
                case short sh: return sh;
                case ushort us: return us;
            }
        }
        return null;
    }

    private static int ReportShapes()
    {
        Console.WriteLine($"  {Interlocked.Read(ref _eventsSeen)} event(s) seen, {EventShapes.Count} distinct shape(s).");
        Console.WriteLine();

        if (EventShapes.IsEmpty)
        {
            Console.WriteLine("  Nothing was emitted. These providers are quiet unless an RDP connection is");
            Console.WriteLine("  active — reconnect while the probe runs, then look for an event whose payload");
            Console.WriteLine("  carries both a channel name and a byte count.");
            return 1;
        }

        foreach (var (key, count) in EventShapes.OrderByDescending(kv => kv.Value).Take(60))
        {
            var parts = key.Split('|');
            Console.WriteLine($"  {count,8}x  {parts[1]}");
            Console.WriteLine($"            provider: {parts[0]}");
            Console.WriteLine($"            payload : {parts[2]}");
        }
        if (EventShapes.Count > 60) Console.WriteLine($"  … and {EventShapes.Count - 60} more shape(s).");
        return 0;
    }

    private static int ReportTotals()
    {
        Console.WriteLine($"  {Interlocked.Read(ref _eventsSeen)} event(s) seen.");
        Console.WriteLine();

        if (Totals.IsEmpty)
        {
            Console.WriteLine("  No client event carried a byte count. Reconnect the session while the probe");
            Console.WriteLine("  runs, or use --discover to see what these providers emit on this build.");
            return 1;
        }

        Console.WriteLine($"  {"CHANNEL",-44} {"BYTES",14}");
        foreach (var (channel, bytes) in Totals.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {channel,-44} {bytes,14:N0}");

        if (Totals.Keys.All(k => k.StartsWith(TransportPrefix, StringComparison.Ordinal)))
        {
            Console.WriteLine();
            Console.WriteLine("  Every row is transport-level: mstsc reported bytes on the wire but never named");
            Console.WriteLine("  a channel, so these cannot be split per DVC. Per-channel numbers come from the");
            Console.WriteLine("  server side — run 'rdpeek-agent dvcwatch' inside the session.");
        }
        return 0;
    }
}
