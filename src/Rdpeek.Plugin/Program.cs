using Rdpeek.Client;
using Rdpeek.Plugin;

// RDPeek client DVC plugin (COM LocalServer32).
//
//   (no args)          usage
//   -Embedding         activated by mstsc — run the COM server
//   channels           print how DVCs are configured on this client
//
// Runtime output → %TEMP%\rdpeek-plugin.log.

bool embedding = args.Any(a =>
    a.TrimStart('-', '/').Equals("Embedding", StringComparison.OrdinalIgnoreCase));

// Built as a WinExe so mstsc's -Embedding activation shows no console window. For the dev CLI
// subcommands, attach to the launching terminal's console so their output is still visible.
if (embedding)
{
    // Keep the plugin alive no matter what the agent does (abrupt disconnect mid-frame, self-update
    // re-exec, crash). A stray background exception must be logged, never take the server down — the
    // listener stays up and keeps accepting the agent whenever it (re)connects.
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        Logger.Log($"UNHANDLED (ignored): {e.ExceptionObject}");
    System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        Logger.Log($"unobserved task (ignored): {e.Exception.Message}");
        e.SetObserved();
    };
    return PluginHost.RunServer();
}

NativeConsole.AttachParent();

if (args.Length > 0 && args[0].Equals("channels", StringComparison.OrdinalIgnoreCase))
{
    Console.Write(ClientChannels.Format(ClientChannels.Collect()));
    return 0;
}

Console.WriteLine("RDPeek client DVC plugin (COM LocalServer32).");
Console.WriteLine();
Console.WriteLine("This exe is activated by the RDP client, not run directly. To install:");
Console.WriteLine("  tools\\register.ps1 -ExePath \"<path to this exe>\"");
Console.WriteLine("Then start or reconnect an RDP session; mstsc loads the plugin.");
Console.WriteLine();
Console.WriteLine("  rdpeek-plugin channels   show how DVCs are configured on this client");
Console.WriteLine("Runtime log: %TEMP%\\rdpeek-plugin.log");
return 0;

static class NativeConsole
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
    // ATTACH_PARENT_PROCESS: write to the terminal that launched us (no-op when there isn't one).
    public static void AttachParent() { try { AttachConsole(-1); } catch { } }
}
