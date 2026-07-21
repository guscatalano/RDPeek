using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
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
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "rdpeek-broker.log"),
                        $"{DateTime.Now:HH:mm:ss.fff}  recv {ev} pid={pid} seq={seq} host='{host}'{Environment.NewLine}");
                }
                catch { }

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
    /// Create the broker pipe with a Low mandatory integrity label so the plugin
    /// (medium integrity, launched by mstsc) can write to it even if the companion is
    /// running elevated. Falls back to a default pipe if ACL setup isn't available.
    /// </summary>
    private static NamedPipeServerStream CreateServer()
    {
        try
        {
            var security = new PipeSecurity();
            // Full pipe access to Authenticated Users (AU) + Low mandatory label (LW),
            // no-write-up (NW) — so medium/high callers can write to a low-labeled pipe.
            security.SetSecurityDescriptorSddlForm("D:(A;;0x1f019f;;;AU)S:(ML;;NW;;;LW)", AccessControlSections.All);
            return NamedPipeServerStreamAcl.Create(
                Broker.PipeName, PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }
        catch
        {
            return new NamedPipeServerStream(
                Broker.PipeName, PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
