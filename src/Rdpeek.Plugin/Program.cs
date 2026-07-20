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

if (embedding)
    return PluginHost.RunServer();

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
