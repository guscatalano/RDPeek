using System.IO.Pipes;

namespace Rdpeek.Client;

/// <summary>
/// Local IPC between the client plugin (running in mstsc's COM server) and the RDPeek
/// companion app. The companion hosts the pipe; each plugin connection reports its
/// state so the companion can show "agent detected / not detected" without log-watching.
///
/// Wire format is one line per message: <c>kind|pid|seq|payload</c>
///   kind: listening | connected | gone | sysinfo | procs
///   pid/seq: identify the plugin process + per-connection instance
///   payload: host name (status kinds) or single-line JSON (sysinfo/procs)
/// The payload is the remainder of the line, so it may itself contain '|'.
/// </summary>
public static class Broker
{
    public const string PipeName = "rdpeek-broker";

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

    /// <summary>Best-effort one-shot report to the companion. No-op if it isn't running.</summary>
    public static void Report(string ev, int pid, int seq, string host = "")
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(500);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(Format(ev, pid, seq, host));
        }
        catch (Exception ex)
        {
            // Best-effort — but log why, so a silently-dropped report can be diagnosed.
            try
            {
                File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rdpeek-brokerclient.log"),
                    $"{DateTime.Now:HH:mm:ss.fff}  report '{ev}' failed: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
