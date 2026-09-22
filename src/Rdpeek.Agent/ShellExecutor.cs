using System.Diagnostics;
using Dvc.Diag.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// Runs a command in cmd.exe or PowerShell and captures its output. This is the one genuinely
/// non-read-only capability, so it is reached only when the agent was started with --allow-shell
/// (enforced in <see cref="AgentCore"/>); it runs as the agent's own user in the RDP session, with
/// no elevation of its own. Output is capped and the process is killed on timeout.
/// </summary>
internal static class ShellExecutor
{
    private const int OutputCap = 100_000;

    public static ShellResult Run(string command, string shell, int timeoutMs)
    {
        var result = new ShellResult { Allowed = true };
        if (string.IsNullOrWhiteSpace(command)) { result.Note = "empty command"; return result; }
        timeoutMs = Math.Clamp(timeoutMs <= 0 ? 30_000 : timeoutMs, 1_000, 120_000);

        bool ps = string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = ps ? "powershell.exe" : "cmd.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (ps)
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }

        try
        {
            using var p = Process.Start(psi);
            if (p is null) { result.Note = "failed to start the shell process"; return result; }

            // Read both streams concurrently so a chatty command can't deadlock on a full pipe.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                result.Note = $"timed out after {timeoutMs} ms — process killed";
            }
            else
            {
                result.ExitCode = p.ExitCode;
            }

            result.Stdout = Cap(stdout.GetAwaiter().GetResult());
            result.Stderr = Cap(stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            result.Note = ex.Message;
        }
        return result;
    }

    private static string Cap(string s) => s.Length > OutputCap ? s[..OutputCap] + "\n…(truncated)" : s;
}
