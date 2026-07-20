using System.IO.Pipes;

namespace Rdpeek.Client;

/// <summary>
/// Local IPC between the client plugin (running in mstsc's COM server) and the RDPeek
/// companion app. The companion hosts the pipe; each plugin connection reports its
/// state so the companion can show "agent detected / not detected" without log-watching.
///
/// Wire format is one line per event: <c>event|pid|seq|host</c>
///   event: listening (channel up, no agent) | connected (agent present) | gone
///   pid/seq: identify the plugin process + per-connection instance
///   host: remote host name once known (from the agent's SysInfo), else empty
/// </summary>
public static class Broker
{
    public const string PipeName = "rdpeek-broker";

    public static string Format(string ev, int pid, int seq, string host = "")
        => $"{ev}|{pid}|{seq}|{host}";

    public static (string ev, int pid, int seq, string host)? Parse(string line)
    {
        var parts = line.Split('|');
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[1], out int pid)) return null;
        if (!int.TryParse(parts[2], out int seq)) return null;
        string host = parts.Length > 3 ? parts[3] : "";
        return (parts[0], pid, seq, host);
    }

    /// <summary>Best-effort one-shot report to the companion. No-op if it isn't running.</summary>
    public static void Report(string ev, int pid, int seq, string host = "")
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(200);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(Format(ev, pid, seq, host));
        }
        catch
        {
            // Companion not running — reporting is best-effort.
        }
    }
}
