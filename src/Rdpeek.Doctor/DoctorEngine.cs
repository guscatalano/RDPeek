using Microsoft.Win32;

namespace Rdpeek.Doctor;

internal enum Severity { Pass, Info, Warn, Fail }

internal enum Activation { Unknown, InprocServer32, LocalServer32, LoadLibrary }

internal sealed record Finding(Severity Severity, string Message);

/// <summary>One registered DVC plugin as found under a Terminal Server Client AddIns key.</summary>
internal sealed class AddInEntry
{
    public required string Source { get; init; }     // e.g. "HKLM\64"
    public required RegistryHive Hive { get; init; }
    public required RegistryView View { get; init; }
    public required string PluginKey { get; init; }  // AddIns subkey name
    public required string NameValue { get; init; }  // CLSID string or DLL path
    public int ValueCount { get; init; }             // registry values on the key

    // Filled in by diagnosis:
    public Activation Activation { get; set; } = Activation.Unknown;
    public string ModulePath { get; set; } = "";
    public Bitness ModuleBitness { get; set; } = Bitness.Unknown;
    public string? Clsid { get; set; }
    public List<Finding> Findings { get; } = new();

    public Severity Worst =>
        Findings.Count == 0 ? Severity.Pass : Findings.Max(f => f.Severity);
}

internal static class DoctorEngine
{
    private const string AddInsPath =
        @"Software\Microsoft\Terminal Server Client\Default\AddIns";

    // mstsc is 64-bit on 64-bit Windows; an in-proc plugin must match.
    private static readonly Bitness HostBitness =
        Environment.Is64BitOperatingSystem ? Bitness.X64 : Bitness.X86;

    private static readonly (RegistryHive Hive, RegistryView View, string Label)[] Sources =
    {
        (RegistryHive.CurrentUser,  RegistryView.Registry64, "HKCU\\64"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM\\64"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM\\32"),
    };

    public static List<AddInEntry> Scan()
    {
        var entries = new List<AddInEntry>();
        foreach (var (hive, view, label) in Sources)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var addins = baseKey.OpenSubKey(AddInsPath);
            if (addins is null) continue;

            foreach (var name in addins.GetSubKeyNames())
            {
                using var k = addins.OpenSubKey(name);
                var val = (k?.GetValue("Name") as string) ?? "";
                entries.Add(new AddInEntry
                {
                    Source = label, Hive = hive, View = view,
                    PluginKey = name, NameValue = val,
                    ValueCount = k?.ValueCount ?? 0,
                });
            }
        }
        return entries;
    }

    public static void Diagnose(List<AddInEntry> entries)
    {
        // CLSIDs visible to 64-bit mstsc, to flag 32-bit-only registrations.
        var clsids64 = entries
            .Where(e => e.View == RegistryView.Registry64)
            .Select(e => e.NameValue)
            .Where(LooksLikeClsid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
            DiagnoseOne(e, clsids64);
    }

    private static void DiagnoseOne(AddInEntry e, HashSet<string> clsids64)
    {
        if (string.IsNullOrWhiteSpace(e.NameValue))
        {
            if (e.ValueCount == 0)
                e.Findings.Add(new(Severity.Info, KnownBuiltins.Contains(e.PluginKey)
                    ? $"Empty AddIn key for built-in channel '{e.PluginKey}' — handled internally by mstsc; nothing to load."
                    : "Empty AddIn key — no 'Name' and no other values (benign placeholder)."));
            else
                e.Findings.Add(new(Severity.Warn, "AddIn key has values but no 'Name' — plugin location is undefined."));
            return;
        }

        if (LooksLikeClsid(e.NameValue))
        {
            e.Clsid = e.NameValue;
            DiagnoseComClsid(e, clsids64);
        }
        else if (e.NameValue.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            e.Activation = Activation.LoadLibrary;
            e.ModulePath = ExpandPath(e.NameValue);
            CheckModuleFile(e, inProc: true);
        }
        else
        {
            e.Findings.Add(new(Severity.Warn, $"'Name' value is neither a CLSID nor a .dll path: {e.NameValue}"));
        }
    }

    private static void DiagnoseComClsid(AddInEntry e, HashSet<string> clsids64)
    {
        var resolved = ResolveClsid(e.Clsid!);
        if (resolved is null)
        {
            e.Findings.Add(new(Severity.Fail, $"CLSID {e.Clsid} is not registered under HKCR\\CLSID (any view)."));
            return;
        }

        var (act, module, foundView) = resolved.Value;
        e.Activation = act;
        e.ModulePath = module;

        if (e.View == RegistryView.Registry32 && !clsids64.Contains(e.NameValue))
            e.Findings.Add(new(Severity.Warn,
                "Registered only in the 32-bit registry view — 64-bit mstsc will not enumerate this AddIn."));

        switch (act)
        {
            case Activation.InprocServer32:
                CheckModuleFile(e, inProc: true);
                e.Findings.Add(new(Severity.Info, $"In-proc DLL resolved from {foundView}: {module}"));
                break;
            case Activation.LocalServer32:
                CheckModuleFile(e, inProc: false); // out-of-proc: bitness need not match mstsc
                e.Findings.Add(new(Severity.Info, $"Out-of-proc server resolved from {foundView}: {module}"));
                break;
            default:
                e.Findings.Add(new(Severity.Warn, $"CLSID found ({foundView}) but has neither InprocServer32 nor LocalServer32."));
                break;
        }

        // Live activation smoke test.
        if (Guid.TryParse(e.Clsid, out var g))
        {
            var (hr, isPlugin) = NativeMethods.ProbeCom(g);
            if (hr < 0)
                e.Findings.Add(new(Severity.Fail, $"CoCreateInstance failed: 0x{hr:X8} ({DescribeHr(hr)})"));
            else if (!isPlugin)
                e.Findings.Add(new(Severity.Warn, "Activates, but does not expose IWTSPlugin (QueryInterface failed)."));
            else
                e.Findings.Add(new(Severity.Pass, "Activates and exposes IWTSPlugin."));
        }
    }

    private static void CheckModuleFile(AddInEntry e, bool inProc)
    {
        if (string.IsNullOrEmpty(e.ModulePath))
        {
            e.Findings.Add(new(Severity.Fail, "Server path is empty."));
            return;
        }
        if (!File.Exists(e.ModulePath))
        {
            e.Findings.Add(new(Severity.Fail, $"Module file not found: {e.ModulePath}"));
            return;
        }

        e.ModuleBitness = NativeMethods.ReadPeBitness(e.ModulePath);
        if (inProc && e.ModuleBitness != Bitness.Unknown && e.ModuleBitness != HostBitness)
            e.Findings.Add(new(Severity.Fail,
                $"Bitness mismatch: module is {e.ModuleBitness}, but mstsc is {HostBitness} — it cannot be loaded in-proc."));
    }

    private static (Activation act, string module, string view)? ResolveClsid(string clsid)
    {
        foreach (var (hive, view, label) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM\\64"),
            (RegistryHive.CurrentUser,  RegistryView.Registry64, "HKCU\\64"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM\\32"),
            (RegistryHive.CurrentUser,  RegistryView.Registry32, "HKCU\\32"),
        })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var clsidKey = baseKey.OpenSubKey($@"Software\Classes\CLSID\{clsid}");
            if (clsidKey is null) continue;

            using var inproc = clsidKey.OpenSubKey("InprocServer32");
            if (inproc is not null)
                return (Activation.InprocServer32, ExpandPath((inproc.GetValue(null) as string) ?? ""), label);

            using var local = clsidKey.OpenSubKey("LocalServer32");
            if (local is not null)
                return (Activation.LocalServer32, ExtractExe((local.GetValue(null) as string) ?? ""), label);

            return (Activation.Unknown, "", label);
        }
        return null;
    }

    // Built-in RDP channels that legitimately appear as empty AddIn placeholders.
    private static readonly HashSet<string> KnownBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "RDPDR", "RDPSND", "CLIPRDR", "DRDYNVC", "MS_T120", "RDPGFX", "TSMF", "RAIL", "RDPEI",
    };

    private static bool LooksLikeClsid(string s) => s.StartsWith('{') && s.EndsWith('}');

    private static string ExpandPath(string p) =>
        Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));

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

    private static string DescribeHr(int hr) => (uint)hr switch
    {
        0x80040154 => "REGDB_E_CLASSNOTREG",
        0x80040111 => "CLASS_E_CLASSNOTAVAILABLE",
        0x8007007E => "ERROR_MOD_NOT_FOUND",
        0x80070005 => "E_ACCESSDENIED",
        _ => "see winerror.h",
    };
}
