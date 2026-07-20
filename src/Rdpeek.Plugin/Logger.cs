namespace Rdpeek.Plugin;

/// <summary>
/// Minimal file logger. The plugin runs inside a COM server with no console, so
/// observable output goes to %TEMP%\rdpeek-plugin.log — the artifact you check after
/// connecting an RDP session to confirm the plugin loaded and talked to the agent.
/// </summary>
internal static class Logger
{
    private static readonly object Gate = new();
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rdpeek-plugin.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never throw into a COM callback
        }
    }
}
