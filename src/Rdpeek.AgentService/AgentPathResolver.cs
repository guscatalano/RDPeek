using Microsoft.Win32;

namespace Rdpeek.AgentService;

/// <summary>
/// Resolves the full path to <c>rdpeek-agent.exe</c> that the service should launch into
/// each session. The core algorithm is pure (a file-exists predicate is injected) so the
/// resolution *order* can be unit-tested without touching the disk or the registry.
///
/// Resolution order (first hit wins):
///   1. Explicit override passed on the service command line / registry ImagePath arg
///      (<c>--agent &lt;path&gt;</c>) — see <see cref="ExplicitOverride"/>.
///   2. Environment variable  RDPEEK_AGENT_EXE.
///   3. Registry             HKLM\SOFTWARE\RDPeek\AgentExe  (REG_SZ, set by the installer).
///   4. Next to the service exe (the default install layout: rdpeek-agent.exe lives in the
///      same folder as rdpeek-agent-service.exe).
/// </summary>
internal static class AgentPathResolver
{
    internal const string AgentFileName = "rdpeek-agent.exe";
    internal const string EnvVar = "RDPEEK_AGENT_EXE";
    internal const string RegistryKeyPath = @"SOFTWARE\RDPeek";
    internal const string RegistryValueName = "AgentExe";

    /// <summary>Explicit override captured from the service args, or null.</summary>
    public static string? ExplicitOverride { get; set; }

    /// <summary>
    /// Pure resolver used by tests: candidates are tried in order, first that
    /// <paramref name="fileExists"/> accepts wins. Returns null if none exist.
    /// </summary>
    public static string? Resolve(
        string? explicitOverride,
        string? envValue,
        string? registryValue,
        string serviceExeDir,
        Func<string, bool> fileExists)
    {
        foreach (var candidate in EnumerateCandidates(explicitOverride, envValue, registryValue, serviceExeDir))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && fileExists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>The ordered candidate list (without existence filtering) — handy for logging.</summary>
    public static IEnumerable<string> EnumerateCandidates(
        string? explicitOverride,
        string? envValue,
        string? registryValue,
        string serviceExeDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitOverride))
            yield return explicitOverride!;
        if (!string.IsNullOrWhiteSpace(envValue))
            yield return envValue!;
        if (!string.IsNullOrWhiteSpace(registryValue))
            yield return registryValue!;
        yield return Path.Combine(serviceExeDir, AgentFileName);
    }

    /// <summary>Live resolution against the real environment, registry and disk.</summary>
    public static string? ResolveLive(string serviceExeDir)
    {
        string? env = Environment.GetEnvironmentVariable(EnvVar);
        string? reg = ReadRegistry();
        return Resolve(ExplicitOverride, env, reg, serviceExeDir, File.Exists);
    }

    private static string? ReadRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath);
            return key?.GetValue(RegistryValueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
