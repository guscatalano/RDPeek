using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rdpeek.Client;

namespace Rdpeek.Companion;

/// <summary>
/// Hosts the local broker pipe that plugin connections report to. Tracks per-connection
/// agent state (keyed by pid:seq) so the UI can show "agent detected / not detected"
/// live, without watching logs.
/// </summary>
public sealed class BrokerServer : IDisposable
{
    public sealed record AgentState(int Pid, int Seq, string Status, string Host);

    private readonly ConcurrentDictionary<string, AgentState> _states = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised (on a background thread) whenever a plugin reports a change.</summary>
    public event Action? Changed;

    public IReadOnlyList<AgentState> Snapshot() => _states.Values.ToList();

    public void Start() => _ = AcceptLoopAsync(_cts.Token);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = CreateServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _ = HandleAsync(server);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server)
    {
        try
        {
            using var reader = new StreamReader(server);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                var parsed = Broker.Parse(line);
                if (parsed is null) continue;

                var (ev, pid, seq, host) = parsed.Value;
                var key = $"{pid}:{seq}";
                if (ev == "gone")
                    _states.TryRemove(key, out _);
                else
                    _states[key] = new AgentState(pid, seq, ev, host);

                Changed?.Invoke();
            }
        }
        catch
        {
            // client closed / malformed — ignore
        }
        finally
        {
            server.Dispose();
        }
    }

    /// <summary>
    /// Create the broker pipe and drop its mandatory integrity label to Low, so the
    /// plugin (medium integrity, launched by mstsc) can write to it even if the
    /// companion happens to run elevated. Best-effort — a plain pipe still works when
    /// the companion runs non-elevated (same integrity as the plugin).
    /// </summary>
    private static NamedPipeServerStream CreateServer()
    {
        var pipe = new NamedPipeServerStream(
            Broker.PipeName, PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try { ApplyLowIntegrityLabel(pipe.SafePipeHandle); } catch { /* best-effort */ }
        return pipe;
    }

    private const int SE_KERNEL_OBJECT = 6;
    private const int LABEL_SECURITY_INFORMATION = 0x10;
    private const uint SDDL_REVISION_1 = 1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr securityDescriptor, out bool saclPresent, out IntPtr sacl, out bool saclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafePipeHandle handle, int objectType, int securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static void ApplyLowIntegrityLabel(SafePipeHandle handle)
    {
        // "S:(ML;;NW;;;LW)" = Low mandatory label, no-write-up. Setting a label at or
        // below the caller's own integrity needs no special privilege.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;;NW;;;LW)", SDDL_REVISION_1, out IntPtr sd, out _))
            return;
        try
        {
            if (GetSecurityDescriptorSacl(sd, out bool present, out IntPtr sacl, out _) && present)
                SetSecurityInfo(handle, SE_KERNEL_OBJECT, LABEL_SECURITY_INFORMATION,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
        }
        finally
        {
            LocalFree(sd);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
