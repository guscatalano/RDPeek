using Rdpeek.Plugin;

// RDPeek client DVC plugin (COM LocalServer32).
//
// The RDP client (mstsc) launches this exe with "-Embedding" to activate the COM
// server. Run directly (no args) for usage. Runtime output → %TEMP%\rdpeek-plugin.log.

bool embedding = args.Any(a =>
    a.TrimStart('-', '/').Equals("Embedding", StringComparison.OrdinalIgnoreCase));

if (embedding)
    return PluginHost.RunServer();

Console.WriteLine("RDPeek client DVC plugin (COM LocalServer32).");
Console.WriteLine();
Console.WriteLine("This exe is activated by the RDP client, not run directly. To install:");
Console.WriteLine("  tools\\register.ps1 -ExePath \"<path to this exe>\"");
Console.WriteLine("Then start or reconnect an RDP session; mstsc loads the plugin.");
Console.WriteLine("Runtime log: %TEMP%\\rdpeek-plugin.log");
return 0;
