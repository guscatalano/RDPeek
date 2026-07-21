namespace Rdpeek.Agent;

/// <summary>
/// Minimal file logger. When the agent runs as a scheduled task there's no console,
/// so diagnostics (and any crash cause) go to %TEMP%\rdpeek-agent.log.
/// </summary>
internal static class Logger
{
    private static readonly object Gate = new();
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rdpeek-agent.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never throw
        }
    }
}
