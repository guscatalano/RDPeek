using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Threading.Channels;

namespace Rdpeek.Client;

/// <summary>
/// Local IPC between the client plugin (running in mstsc's COM server) and the RDPeek
/// companion app. The companion hosts the pipe; each plugin connection reports its
/// state so the companion can show "agent detected / not detected" without log-watching.
///
/// Wire format is one line per message: <c>kind|pid|seq|payload</c>
///   kind: listening | connected | gone | sysinfo | procs | net | sessions | services
///         | perf | counters | link
///   pid/seq: identify the plugin process + per-connection instance
///   payload: host name (status kinds) or single-line JSON (sysinfo/procs)
/// The payload is the remainder of the line, so it may itself contain '|'.
///
/// <para>
/// <b>Report never blocks.</b> It is called from <c>IWTSPlugin.Initialize</c>,
/// <c>OnNewChannelConnection</c>, <c>Disconnected</c> and <c>Terminated</c> — all of which
/// mstsc invokes synchronously across a COM process boundary, so any wait here stalls the
/// RDP client itself. Report only enqueues; a background sender owns the pipe.
/// </para>
/// </summary>
public static class Broker
{
    public const string PipeName = "rdpeek-broker";

    // A pipe that does not exist can be ruled out in ~2ms this way. NamedPipeClientStream
    // .Connect(timeout) cannot: it spins until the timeout expires, because the server may
    // still show up. That behaviour is what used to hang mstsc for 500ms per report.
    private const string PipePath = @"\\.\pipe\" + PipeName;

    private const int ConnectTimeoutMs = 50;     // only reached when the pipe already exists
    private const int MinRetryMs = 250;
    private const int MaxRetryMs = 5_000;
    private const int QueueCapacity = 256;

    // DropOldest: if the companion is away, stale telemetry is worth less than staying
    // bounded. Fresh data follows within one poll cycle once it reconnects.
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private static int _senderStarted;

    /// <summary>
    /// Latest connection-status line, replayed whenever the pipe is (re)established so a
    /// companion started after the RDP connection still sees the right state.
    /// </summary>
    private static volatile string? _lastStatus;

    public static string Format(string kind, int pid, int seq, string payload = "")
        => $"{kind}|{pid}|{seq}|{payload}";

    public static (string kind, int pid, int seq, string payload)? Parse(string line)
    {
        var parts = line.Split('|', 4); // payload (parts[3]) keeps any embedded '|'
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[1], out int pid)) return null;
        if (!int.TryParse(parts[2], out int seq)) return null;
        string payload = parts.Length > 3 ? parts[3] : "";
        return (parts[0], pid, seq, payload);
    }

    /// <summary>
    /// Best-effort report to the companion. Returns immediately — never connects, never
    /// waits, never throws. No-op in effect if the companion isn't running.
    /// </summary>
    public static void Report(string ev, int pid, int seq, string host = "")
    {
        try
        {
            string line = Format(ev, pid, seq, host);
            if (IsStatus(ev)) _lastStatus = line;

            EnsureSenderStarted();
            Queue.Writer.TryWrite(line); // bounded + DropOldest, so this cannot block
        }
        catch
        {
            // Reporting is diagnostics; it must never disturb a COM callback.
        }
    }

    private static bool IsStatus(string ev)
        => ev is "listening" or "connected" or "gone";

    private static void EnsureSenderStarted()
    {
        if (Interlocked.Exchange(ref _senderStarted, 1) == 0)
        {
            _ = Task.Run(SendLoopAsync);
        }
    }

    private static async Task SendLoopAsync()
    {
        StreamWriter? writer = null;
        NamedPipeClientStream? pipe = null;
        int retryMs = MinRetryMs;

        while (true)
        {
            try
            {
                // Connected: sleep until there is something to send. Disconnected: also wake
                // on the retry timer, so a companion that starts later gets picked up even
                // when this plugin has nothing new to say.
                using (var wake = writer is null ? new CancellationTokenSource(retryMs) : null)
                {
                    try
                    {
                        await Queue.Reader.WaitToReadAsync(wake?.Token ?? CancellationToken.None)
                                          .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // retry tick, not an error
                    }
                }

                if (writer is null)
                {
                    if (TryConnect(out pipe, out writer))
                    {
                        retryMs = MinRetryMs;

                        // Re-announce state to a companion that missed the original report.
                        var status = _lastStatus;
                        if (status is not null && !TryWrite(writer, status))
                        {
                            Disconnect(ref pipe, ref writer);
                        }
                    }
                    else
                    {
                        retryMs = Math.Min(retryMs * 2, MaxRetryMs);
                    }
                }

                while (Queue.Reader.TryRead(out var line))
                {
                    // Companion away: drop rather than hold the line. The poll loop resends
                    // fresh data every few seconds, and status is replayed on reconnect.
                    if (writer is null) continue;

                    if (!TryWrite(writer, line))
                    {
                        Disconnect(ref pipe, ref writer);
                    }
                }
            }
            catch
            {
                Disconnect(ref pipe, ref writer);
                retryMs = Math.Min(retryMs * 2, MaxRetryMs);
            }
        }
    }

    private static bool TryConnect(
        [NotNullWhen(true)] out NamedPipeClientStream? pipe,
        [NotNullWhen(true)] out StreamWriter? writer)
    {
        pipe = null;
        writer = null;

        try
        {
            // The cheap negative check — this is what keeps a missing companion from costing
            // anything. Only pay for a real connect attempt once the pipe actually exists.
            if (!File.Exists(PipePath)) return false;

            var candidate = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            try
            {
                candidate.Connect(ConnectTimeoutMs);
            }
            catch
            {
                candidate.Dispose();
                return false;
            }

            pipe = candidate;
            writer = new StreamWriter(candidate) { AutoFlush = true };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWrite(StreamWriter writer, string line)
    {
        try
        {
            writer.WriteLine(line);
            return true;
        }
        catch
        {
            return false; // companion went away mid-stream
        }
    }

    private static void Disconnect(ref NamedPipeClientStream? pipe, ref StreamWriter? writer)
    {
        try { writer?.Dispose(); } catch { }
        try { pipe?.Dispose(); } catch { }
        writer = null;
        pipe = null;
    }
}
