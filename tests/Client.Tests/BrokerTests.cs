using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Rdpeek.Client;
using Xunit;

namespace Client.Tests;

/// <summary>
/// Broker is the plugin's send path to the companion, and it is called from COM callbacks
/// that mstsc invokes synchronously — so the property under test is that reporting never
/// blocks the caller, whatever the companion is doing.
///
/// One class, so xUnit runs these sequentially: Broker is static (one queue, one sender,
/// one well-known pipe name) and parallel tests would fight over the pipe.
/// </summary>
public class BrokerTests
{
    /// <summary>Stands in for the companion: hosts the broker pipe and records what arrives.</summary>
    private sealed class FakeCompanion : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public ConcurrentQueue<string> Lines { get; } = new();

        public FakeCompanion() => _ = AcceptLoopAsync(_cts.Token);

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        Broker.PipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    _ = ReadAsync(server, ct);
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch
                {
                    server?.Dispose();
                    try { await Task.Delay(50, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }

        private async Task ReadAsync(NamedPipeServerStream server, CancellationToken ct)
        {
            try
            {
                using (server)
                using (var reader = new StreamReader(server))
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                    {
                        Lines.Enqueue(line);
                    }
                }
            }
            catch { /* closed */ }
        }

        public bool Saw(string kind, int seq) =>
            Lines.Any(l => Broker.Parse(l) is { } p && p.kind == kind && p.seq == seq);

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    // Each test uses its own seq so leftover state from a previous test can't satisfy it.
    private static int _nextSeq = 1000;
    private static int NextSeq() => Interlocked.Increment(ref _nextSeq);

    [Fact]
    public void Report_ReturnsImmediately_WhenCompanionIsAbsent()
    {
        int seq = NextSeq();

        // The regression this guards: Connect(500) against a pipe with no server does not
        // fail fast, it spins for the whole timeout. Twenty of those was ~10s of mstsc stall.
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            Broker.Report("listening", 4242, seq);
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 250,
            $"Report blocked the caller for {sw.ElapsedMilliseconds}ms across 20 calls.");
    }

    [Fact]
    public void Report_DoesNotThrow_WhenCompanionIsAbsent()
    {
        Broker.Report("sysinfo", 4242, NextSeq(), "{\"hostName\":\"x\"}");
    }

    [Fact]
    public async Task Report_DeliversToCompanion_WhenItIsRunning()
    {
        int seq = NextSeq();
        using var companion = new FakeCompanion();

        // Keep reporting: the sender may need a retry tick to notice the pipe appeared.
        Assert.True(await Eventually(() =>
        {
            Broker.Report("connected", 4242, seq, "REMOTEHOST");
            return companion.Saw("connected", seq);
        }), "companion never received the report");
    }

    [Fact]
    public async Task Report_ReplaysStatus_WhenCompanionStartsLate()
    {
        int seq = NextSeq();

        // Status reported while nothing is listening — this is the Initialize case.
        Broker.Report("listening", 9001, seq);
        await Task.Delay(700); // let at least one connect attempt fail

        using var companion = new FakeCompanion();

        // Nothing reports it again; the sender must reconnect on its own and re-announce,
        // otherwise a companion opened after the RDP session would show nothing.
        Assert.True(await Eventually(() => companion.Saw("listening", seq)),
            "status was not replayed to a companion that started late");
    }

    [Fact]
    public async Task Report_Reconnects_AfterCompanionRestarts()
    {
        int firstSeq = NextSeq();
        using (var first = new FakeCompanion())
        {
            Assert.True(await Eventually(() =>
            {
                Broker.Report("connected", 4242, firstSeq);
                return first.Saw("connected", firstSeq);
            }), "first companion never received the report");
        }

        // Companion gone; its pipe is dead under the sender.
        await Task.Delay(300);

        int secondSeq = NextSeq();
        using var second = new FakeCompanion();

        Assert.True(await Eventually(() =>
        {
            Broker.Report("connected", 4242, secondSeq);
            return second.Saw("connected", secondSeq);
        }), "sender did not reconnect after the companion restarted");
    }
}
