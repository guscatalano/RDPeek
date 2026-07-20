using System.Collections.Concurrent;
using System.IO.Pipes;
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
                var server = new NamedPipeServerStream(
                    Broker.PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

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

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
