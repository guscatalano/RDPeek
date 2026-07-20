using System.Text;
using Dvc.Diag.Protocol;
using Microsoft.Win32;

namespace Rdpeek.Client;

/// <summary>How one dynamic virtual channel is configured on this client.</summary>
public sealed record DvcConfig(
    string Name,
    string Source,        // HKCU\64, HKLM\64, HKLM\32
    string Activation,    // InprocServer32 | LocalServer32 | LoadLibrary | (built-in) | (unresolved)
    string Clsid,
    string ModulePath,
    string Bitness,
    bool IsBuiltin,
    string Description);

/// <summary>
/// Enumerates the DVC plugins configured on the client (the RDP client machine): the
/// Terminal Server Client AddIns registrations, resolved to activation model, module
/// and bitness, plus recognition of the built-in channels mstsc handles internally.
/// This is the client-side "how are my DVCs configured" view.
/// </summary>
public static class ClientChannels
{
    private const string AddInsPath = @"Software\Microsoft\Terminal Server Client\Default\AddIns";

    private static readonly (RegistryHive Hive, RegistryView View, string Label)[] Sources =
    {
        (RegistryHive.CurrentUser,  RegistryView.Registry64, "HKCU\\64"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM\\64"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM\\32"),
    };

    // Built-in RDP channels mstsc handles internally (usually empty AddIn placeholders).
    private static readonly Dictionary<string, string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RDPDR"]   = "Device / drive / port redirection",
        ["RDPSND"]  = "Audio output redirection",
        ["CLIPRDR"] = "Clipboard redirection",
        ["DRDYNVC"] = "Dynamic virtual channel multiplexer",
        ["RAIL"]    = "RemoteApp integrated apps",
        ["RDPEI"]   = "Touch / pen input",
        ["RDPGFX"]  = "Graphics pipeline (RemoteFX/AVC)",
        ["RDPEMSC"] = "Mouse cursor",
        ["MS_T120"] = "Reserved control channel",
        ["TSMF"]    = "Multimedia redirection",
        ["URBDR"]   = "USB device redirection",
        ["ECHO"]    = "Connection echo/keepalive",
    };

    public static List<DvcConfig> Collect()
    {
        var result = new List<DvcConfig>();

        foreach (var (hive, view, label) in Sources)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var addins = baseKey.OpenSubKey(AddInsPath);
            if (addins is null) continue;

            foreach (var name in addins.GetSubKeyNames())
            {
                using var k = addins.OpenSubKey(name);
                var nameValue = (k?.GetValue("Name") as string) ?? "";
                bool builtin = Builtins.TryGetValue(name, out var desc);

                if (string.IsNullOrWhiteSpace(nameValue))
                {
                    // Empty AddIn key — a built-in placeholder or an unconfigured entry.
                    result.Add(new DvcConfig(name, label,
                        builtin ? "(built-in)" : "(empty)", "", "", "", builtin,
                        builtin ? desc! : "Empty AddIn key — no plugin configured"));
                    continue;
                }

                if (LooksLikeClsid(nameValue))
                {
                    var (act, module) = ResolveClsid(nameValue);
                    var bitness = string.IsNullOrEmpty(module) ? "" : ReadBitness(module);
                    result.Add(new DvcConfig(name, label, act, nameValue, module, bitness,
                        builtin, builtin ? desc! : "COM plugin"));
                }
                else if (nameValue.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    var module = Environment.ExpandEnvironmentVariables(nameValue);
                    result.Add(new DvcConfig(name, label, "LoadLibrary", "", module,
                        ReadBitness(module), builtin, builtin ? desc! : "Direct DLL plugin"));
                }
                else
                {
                    result.Add(new DvcConfig(name, label, "(unknown)", "", nameValue, "",
                        builtin, "Unrecognized 'Name' value"));
                }
            }
        }

        return result;
    }

    /// <summary>Project the client configuration into the wire <see cref="ChannelRoster"/>.</summary>
    public static ChannelRoster ToRoster(IEnumerable<DvcConfig> configs)
    {
        var roster = new ChannelRoster();
        foreach (var c in configs)
        {
            roster.Entries.Add(new ChannelRoster.Types.Entry
            {
                Name = c.Name,
                Registered = true,        // present in the AddIns registry
                Active = false,           // liveness needs ETW/live-channel data (later)
                Activation = c.Activation,
                ModulePath = c.ModulePath,
            });
        }
        return roster;
    }

    /// <summary>Human-readable report for the CLI / plugin log.</summary>
    public static string Format(IEnumerable<DvcConfig> configs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Configured Dynamic Virtual Channels on this client:");
        sb.AppendLine();
        foreach (var c in configs)
        {
            var tag = c.IsBuiltin && c.Activation != "(built-in)" ? "  (built-in)" : "";
            sb.AppendLine($"  {c.Name,-14} [{c.Source}]  {c.Activation}{tag}");
            if (!string.IsNullOrEmpty(c.Description)) sb.AppendLine($"       {c.Description}");
            if (!string.IsNullOrEmpty(c.Clsid))       sb.AppendLine($"       CLSID : {c.Clsid}");
            if (!string.IsNullOrEmpty(c.ModulePath))
                sb.AppendLine($"       Module: {c.ModulePath}{(string.IsNullOrEmpty(c.Bitness) ? "" : $" ({c.Bitness})")}");
        }
        return sb.ToString();
    }

    private static bool LooksLikeClsid(string s) => s.StartsWith('{') && s.EndsWith('}');

    private static (string activation, string module) ResolveClsid(string clsid)
    {
        foreach (var (hive, view, _) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, ""),
            (RegistryHive.CurrentUser,  RegistryView.Registry64, ""),
            (RegistryHive.LocalMachine, RegistryView.Registry32, ""),
            (RegistryHive.CurrentUser,  RegistryView.Registry32, ""),
        })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var clsidKey = baseKey.OpenSubKey($@"Software\Classes\CLSID\{clsid}");
            if (clsidKey is null) continue;

            using var inproc = clsidKey.OpenSubKey("InprocServer32");
            if (inproc is not null)
                return ("InprocServer32", Environment.ExpandEnvironmentVariables((inproc.GetValue(null) as string) ?? "").Trim('"'));

            using var local = clsidKey.OpenSubKey("LocalServer32");
            if (local is not null)
                return ("LocalServer32", ExtractExe((local.GetValue(null) as string) ?? ""));

            return ("(no server)", "");
        }
        return ("(unresolved CLSID)", "");
    }

    private static string ExtractExe(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            command = end > 0 ? command[1..end] : command.Trim('"');
        }
        else
        {
            int sp = command.IndexOf(' ');
            if (sp > 0) command = command[..sp];
        }
        return Environment.ExpandEnvironmentVariables(command);
    }

    private static string ReadBitness(string path)
    {
        try
        {
            if (!File.Exists(path)) return "missing";
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return "?";
            fs.Position = 0x3C;
            fs.Position = br.ReadInt32();
            if (br.ReadUInt32() != 0x0000_4550) return "?";
            return br.ReadUInt16() switch
            {
                0x014C => "X86",
                0x8664 => "X64",
                0xAA64 => "Arm64",
                _ => "?",
            };
        }
        catch { return "?"; }
    }
}
