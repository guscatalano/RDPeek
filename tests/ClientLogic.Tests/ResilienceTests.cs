using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using Rdpeek.Client;
using Xunit;

namespace ClientLogic.Tests;

/// <summary>
/// Regression tests for the "nobody dies while the other side is absent" contract. The agent, the
/// client plugin and the companion are each designed to wait/retry for the others rather than fail —
/// so a component started (or restarted) in any order still converges. These pin the two directions
/// we can exercise without a live RDP session:
///  - the companion's broker SERVER tolerates no client, a late client, a disconnect+reconnect, and
///    a "gone" notice;
///  - the plugin's <see cref="Broker"/> report path never throws when no companion is running (it is
///    called from COM callbacks, so a throw there would disturb the RDP client itself).
/// The agent's own retry loop (<c>ServeLoop</c> re-opening the DVC every second until a client plugin
/// listens) is covered by inspection — it P/Invokes the WTS API, which isn't unit-testable here.
/// </summary>
public class ResilienceTests
{
    private static string UniquePipe() => "rdpeek-test-" + Guid.NewGuid().ToString("N");

    /// <summary>Connect a throwaway client, write the given wire lines, then disconnect (mimicking one
    /// plugin connection reporting and going away).</summary>
    private static async Task ConnectAndWriteAsync(string pipeName, params string[] lines)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        var writer = new StreamWriter(client) { AutoFlush = true };
        foreach (var line in lines) await writer.WriteLineAsync(line);
        await Task.Delay(150); // let the server read before `using` closes the pipe
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(25);
        return condition();
    }

    [Fact]
    public async Task Server_with_no_client_stays_alive_and_empty()
    {
        using var server = new BrokerServer(UniquePipe());
        server.Start();
        await Task.Delay(300);
        // No plugin has connected: the companion simply waits — no tracked state, no crash.
        Assert.Empty(server.Snapshot());
    }

    [Fact]
    public async Task Server_tracks_a_plugin_that_connects_after_it_started()
    {
        var pipe = UniquePipe();
        using var server = new BrokerServer(pipe);
        server.Start();

        await ConnectAndWriteAsync(pipe, Broker.Format("listening", 4242, 1, "HOST-A"));

        bool seen = await WaitForAsync(() =>
            server.Snapshot().Any(s => s.Pid == 4242 && s.Seq == 1 && s.Status == "listening"));
        Assert.True(seen, "the companion should pick up a plugin that connects after it started");
    }

    [Fact]
    public async Task Server_survives_plugin_disconnect_then_reconnect()
    {
        var pipe = UniquePipe();
        using var server = new BrokerServer(pipe);
        server.Start();

        // A plugin connects, reports, then disconnects (ConnectAndWriteAsync closes its pipe).
        await ConnectAndWriteAsync(pipe, Broker.Format("listening", 4242, 1, "HOST-A"));
        Assert.True(await WaitForAsync(() => server.Snapshot().Any(s => s.Status == "listening")));

        // A brand-new connection (the reconnect) reports again — the server must still be accepting.
        await ConnectAndWriteAsync(pipe, Broker.Format("connected", 4242, 1, "HOST-A"));
        bool reconnected = await WaitForAsync(() =>
            server.Snapshot().Any(s => s.Pid == 4242 && s.Status == "connected"));
        Assert.True(reconnected, "the server should survive a plugin disconnect and accept a reconnect");
    }

    [Fact]
    public async Task Server_gone_notice_removes_the_connection()
    {
        var pipe = UniquePipe();
        using var server = new BrokerServer(pipe);
        server.Start();

        await ConnectAndWriteAsync(pipe, Broker.Format("connected", 4242, 7, "HOST-A"));
        Assert.True(await WaitForAsync(() => server.Snapshot().Any(s => s.Seq == 7)));

        await ConnectAndWriteAsync(pipe, Broker.Format("gone", 4242, 7));
        Assert.True(await WaitForAsync(() => server.Snapshot().All(s => s.Seq != 7)),
            "a 'gone' notice should drop that connection's tracked state");
    }

    [Fact]
    public async Task Plugin_report_made_before_the_companion_exists_is_replayed_when_it_connects()
    {
        // Point the plugin-side Broker at a private pipe so this can't collide with a real companion.
        var pipe = UniquePipe();
        var saved = Broker.ActivePipeName;
        try
        {
            Broker.ActivePipeName = pipe;

            // The RDP session comes up and the plugin reports BEFORE any companion is running.
            Broker.Report("listening", 31337, 5, "HOST-LATE");

            // Now the companion starts (e.g. the user launches it after connecting).
            using var server = new BrokerServer(pipe);
            server.Start();

            // The plugin's background sender must connect and REPLAY the cached status — otherwise a
            // companion started after the RDP connection would never learn this session exists.
            bool replayed = await WaitForAsync(
                () => server.Snapshot().Any(s => s.Pid == 31337 && s.Seq == 5 && s.Status == "listening"),
                timeoutMs: 8000);
            Assert.True(replayed, "a report made before the companion started must be replayed on connect");
        }
        finally
        {
            Broker.Report("gone", 31337, 5);   // don't leave this in the shared cache for other tests
            Broker.ActivePipeName = saved;
        }
    }

    [Fact]
    public void Plugin_Report_and_Send_never_throw_without_a_companion()
    {
        // Report/Send run inside IWTSPlugin COM callbacks. With no companion listening they must be
        // silent no-ops (enqueue + a background retry), never throwing — a throw here would propagate
        // into mstsc/msrdc across the COM boundary.
        var ex = Record.Exception(() =>
        {
            for (int seq = 0; seq < 50; seq++)
            {
                Broker.Report("listening", 999, seq, "HOST");
                Broker.Report("connected", 999, seq, "HOST");
                Broker.Send(Broker.Format("caps", 999, seq, "shell screenshot"));
                Broker.Report("gone", 999, seq);
            }
        });
        Assert.Null(ex);
    }
}
