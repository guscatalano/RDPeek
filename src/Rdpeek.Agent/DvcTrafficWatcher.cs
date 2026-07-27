using System.Collections.Concurrent;
using Dvc.Diag.Protocol;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Rdpeek.Agent;

/// <summary>
/// Per-DVC traffic counters, sourced from the RDP server's own ETW provider.
///
/// Ported from https://github.com/guscatalano/RDP_DVC_Watcher: the server fires a
/// "write flush" trace event per channel write carrying the channel name and payload
/// size, so summing those events yields per-channel byte totals — the numbers the
/// dashboard wants long before Windows ships per-DVC perfmon counters.
///
/// Caveats this inherits from the original tool, and why the agent must not depend on it:
///   * Server-side only. It sees what the session host writes toward the client; the
///     client half of the same channel is invisible here.
///   * Partial. Only writes that go through the flush path are counted, so treat the
///     totals as a live traffic signal, not an audited byte count.
///   * Needs an ETW session, which needs Administrator (or Performance Log Users).
///     An unelevated agent degrades to "unavailable" with a reason, never a crash.
/// </summary>
internal sealed class DvcTrafficWatcher
{
    /// <summary>Microsoft-Windows-RemoteDesktopServices DVC provider (server side).</summary>
    private static readonly Guid Provider = new("8375996d-5801-4fe9-b0ae-f5c428758960");

    private const string SessionName = "RDPeek-DVCWatch";

    public static DvcTrafficWatcher Instance { get; } = new();

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, long> _totals = new(StringComparer.OrdinalIgnoreCase);
    private readonly RateTracker _sendRates = new();

    private TraceEventSession? _session;
    private bool _started;
    private long _events;
    private string _status = "not started";

    /// <summary>Human-readable state, shown when there is nothing to chart.</summary>
    public string Status { get { lock (_gate) return _status; } }

    public bool IsAvailable { get { lock (_gate) return _session is not null; } }

    /// <summary>
    /// Start the ETW session on first use. Idempotent, never throws: returns false and
    /// records <see cref="Status"/> when tracing isn't available in this context.
    /// </summary>
    public bool EnsureStarted()
    {
        lock (_gate)
        {
            if (_started) return _session is not null;
            _started = true;

            if (TraceEventSession.IsElevated() != true)
            {
                _status = "unavailable — ETW tracing needs an elevated agent (Administrator).";
                Logger.Log($"dvc watcher: {_status}");
                return false;
            }

            try
            {
                // A same-named session left behind by a crashed run is restarted, not
                // duplicated — that is the default Create behavior.
                var session = new TraceEventSession(SessionName) { StopOnDispose = true };
                session.EnableProvider(Provider);
                session.Source.Dynamic.All += OnEvent;

                var pump = new Thread(() => Pump(session))
                {
                    IsBackground = true,
                    Name = "rdpeek-dvc-etw",
                };
                pump.Start();

                _session = session;
                _status = "listening";
                Logger.Log($"dvc watcher: ETW session '{SessionName}' started.");
                return true;
            }
            catch (Exception ex)
            {
                _status = $"unavailable — {ex.GetType().Name}: {ex.Message}";
                Logger.Log($"dvc watcher: start failed — {ex}");
                _session = null;
                return false;
            }
        }
    }

    private void Pump(TraceEventSession session)
    {
        try
        {
            session.Source.Process(); // blocks until the session is disposed
        }
        catch (Exception ex)
        {
            Logger.Log($"dvc watcher: pump stopped — {ex.Message}");
            lock (_gate) _status = $"stopped — {ex.Message}";
        }
    }

    private void OnEvent(TraceEvent ev)
    {
        try
        {
            // ToString() renders the payload as attributes, which is where the channel
            // name and size live. It is the proven path from the original tool; keep the
            // parse itself in DvcTrafficParser so it stays testable.
            if (!DvcTrafficParser.TryParseWriteFlush(ev.ToString(), out string channel, out long bytes)) return;

            _totals.AddOrUpdate(channel, bytes, (_, total) => total + bytes);
            Interlocked.Increment(ref _events);
        }
        catch
        {
            // A malformed event must never take down the trace pump.
        }
    }

    /// <summary>
    /// Cumulative bytes per channel plus the send rate since the previous snapshot.
    /// Starts the watcher on first call, so a counter request works even if the client
    /// skipped the handshake.
    /// </summary>
    public CounterSample Snapshot()
    {
        bool available = EnsureStarted();
        long nowTicks = DateTime.UtcNow.Ticks;

        var sample = new CounterSample
        {
            SampledUtcTicks = nowTicks,
            Source = "etw",
        };

        lock (_gate)
        {
            foreach (var (name, total) in _totals.ToArray().OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                sample.Channels.Add(new CounterSample.Types.ChannelCounters
                {
                    Name = name,
                    BytesSent = (ulong)total,
                    SendRateBps = _sendRates.Sample(name, total, nowTicks),
                });

            sample.Note = sample.Channels.Count > 0
                ? ""
                : available
                    ? "No DVC writes observed yet — traffic appears once a channel is active on this session host."
                    : _status;
        }

        return sample;
    }

    /// <summary>The channels this agent has actually seen carry traffic.</summary>
    public ChannelRoster Roster()
    {
        EnsureStarted();
        var roster = new ChannelRoster();
        foreach (var name in _totals.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            roster.Entries.Add(new ChannelRoster.Types.Entry { Name = name, Active = true });
        return roster;
    }

    public long EventCount => Interlocked.Read(ref _events);

    public void Stop()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            _status = "stopped";
        }
    }
}
