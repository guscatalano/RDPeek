namespace Rdpeek.Bootstrap;

// RDPeek bootstrap installer + connect probe.
//
// Default (no args): a throwaway GUI that hosts the RDP ActiveX control (mstscax.dll)
// to provision the RDPeek agent on a remote host you own — connect once with drive
// redirection and a "start program" that registers the on-connect scheduled task, then
// the session logs off. An alternative to tools/install-agent-web.ps1.
//
//   rdpeek-bootstrap                         launch the installer GUI
//   rdpeek-bootstrap --connect host[:port]      connect (NLA off) and hold, so a registered
//                    [--hold <seconds>]          RDPeek plugin loads its DVC — used to drive
//                    [--plugin-dll <path>]       the connection against the mock RDP server.
//                    [--detect-timeout <secs>]
//                    [--force-fallback]
//                    [--fallback-command <cmd>]
//
// The hosted control does NOT load the client COM AddIns that mstsc.exe does; --plugin-dll
// points it at a DVC plugin DLL exporting VirtualChannelGetInstance (e.g. rdpeek-vc-shim.dll,
// which CoCreateInstance's the registered RDPeek COM plugin) so the plugin loads headlessly.
//
// Provisioning strategy: AlternateShell (from --agent-folder/--script) is primary; the agent's
// check-in is detected (broker report OR plugin log). If it doesn't arrive within --detect-timeout,
// the probe falls back to injecting Win+R + the command into the session. --force-fallback skips
// detection (to exercise the injection); --fallback-command overrides what gets typed.
//
// Exit codes for --connect: 0 = reached "connected", 2 = timed out, 3 = error.

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var target = GetOption(args, "--connect");
        if (target is not null)
        {
            int hold = int.TryParse(GetOption(args, "--hold"), out var h) ? h : 8;
            // Optional: also send the installer's real drive-redirect + StartProgram, so a
            // server can verify what the installer transmits.
            var agentFolder = GetOption(args, "--agent-folder");
            var script = GetOption(args, "--script");
            var pluginDll = GetOption(args, "--plugin-dll");
            int detect = int.TryParse(GetOption(args, "--detect-timeout"), out var d) ? d : 0;
            bool forceFallback = Array.IndexOf(args, "--force-fallback") >= 0;
            var fallbackCommand = GetOption(args, "--fallback-command");
            using var probe = new ConnectProbe(target, hold, agentFolder, script, pluginDll,
                detect, forceFallback, fallbackCommand);
            Application.Run(probe);
            return probe.ExitCode;
        }

        Application.Run(new BootstrapForm());
        return 0;
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
