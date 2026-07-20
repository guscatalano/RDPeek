using System.Security.Cryptography;
using Dvc.Diag.Protocol;
using Rdpeek.Protocol;
using Xunit;

namespace Conformance;

/// <summary>
/// End-to-end contract tests over the in-proc loopback (no RDP). Proves the
/// handshake, sysinfo request/response + subscription, and a resumable file pull
/// with sha256 integrity — including detection of an induced mid-stream corruption.
/// </summary>
public class ConformanceTests
{
    [Fact]
    public async Task Handshake_returns_capabilities()
    {
        var link = new LoopbackLink();
        _ = new MockAgent(link.Agent);

        var reply = await link.Client.Router.RequestAsync(
            new Envelope { Hello = new Hello { ProtocolVersion = 1, ClientBuild = "viewer/test" } });

        Assert.Equal(Envelope.BodyOneofCase.Capabilities, reply.BodyCase);
        Assert.True(reply.Capabilities.Sysinfo);
        Assert.True(reply.Capabilities.FilePull);
        Assert.False(reply.Capabilities.ProcessKill); // read-only mock
        Assert.Contains("C:\\diag", reply.Capabilities.FileRoots);
    }

    [Fact]
    public async Task SysInfo_oneshot_returns_snapshot()
    {
        var link = new LoopbackLink();
        _ = new MockAgent(link.Agent);

        var reply = await link.Client.Router.RequestAsync(
            new Envelope { SysinfoRequest = new SysInfoRequest { IntervalMs = 0 } });

        Assert.Equal("REMOTE01", reply.SysinfoSnapshot.HostName);
        Assert.Equal(26200u, reply.SysinfoSnapshot.OsBuild);
    }

    [Fact]
    public async Task SysInfo_subscription_pushes_multiple_snapshots()
    {
        var link = new LoopbackLink();
        _ = new MockAgent(link.Agent) { SubscriptionPushes = 4 };

        int pushes = 0;
        link.Client.Router.OnMessage += e =>
        {
            if (e.BodyCase == Envelope.BodyOneofCase.SysinfoSnapshot) pushes++;
        };

        await link.Client.Router.PushAsync(
            new Envelope { SysinfoRequest = new SysInfoRequest { IntervalMs = 1000 } });

        Assert.Equal(4, pushes);
    }

    [Fact]
    public async Task Process_list_returns_remote_processes()
    {
        var link = new LoopbackLink();
        _ = new MockAgent(link.Agent);

        var reply = await link.Client.Router.RequestAsync(
            new Envelope { ProcessListRequest = new ProcessListRequest() });

        Assert.Equal(2, reply.ProcessList.Processes.Count);
        Assert.Contains(reply.ProcessList.Processes, p => p.ImageName == "explorer.exe");
    }

    [Fact]
    public async Task File_pull_transfers_and_verifies_sha256()
    {
        var link = new LoopbackLink();
        var agent = new MockAgent(link.Agent) { ChunkSize = 4096 };
        var content = RandomBytes(10_000); // spans multiple chunks
        agent.Files["C:\\diag\\app.log"] = content;

        var (ok, data, acks) = await PullAsync(link.Client, "C:\\diag\\app.log");

        Assert.True(ok);                                  // sha256 matched
        Assert.Equal(content, data);                      // bytes identical
        Assert.True(acks > 0);                            // client acked chunks
    }

    [Fact]
    public async Task File_pull_missing_file_returns_error()
    {
        var link = new LoopbackLink();
        _ = new MockAgent(link.Agent);

        var reply = await link.Client.Router.RequestAsync(new Envelope
        {
            FileOpen = new FileOpen
            {
                TransferId = 1,
                Direction = FileOpen.Types.Direction.Pull,
                Path = "C:\\diag\\nope.log",
            },
        });

        Assert.Equal(Envelope.BodyOneofCase.Error, reply.BodyCase);
        Assert.Equal(Error.Types.Code.NotFound, reply.Error.Code);
    }

    [Fact]
    public async Task File_pull_detects_induced_corruption()
    {
        var link = new LoopbackLink();
        var agent = new MockAgent(link.Agent) { ChunkSize = 4096, CorruptChunkIndex = 1 };
        var content = RandomBytes(10_000);
        agent.Files["C:\\diag\\app.log"] = content;

        var (ok, data, _) = await PullAsync(link.Client, "C:\\diag\\app.log");

        Assert.False(ok);                    // sha256 mismatch caught
        Assert.NotEqual(content, data);      // received bytes differ from source
    }

    // --- client-side pull helper: request, accumulate chunks, ack, verify digest ---
    private static async Task<(bool ok, byte[] data, int acks)> PullAsync(DiagEndpoint client, string path)
    {
        const ulong tid = 1;
        var buffer = new MemoryStream();
        byte[]? expected = null;
        int acks = 0;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnMsg(Envelope e)
        {
            switch (e.BodyCase)
            {
                case Envelope.BodyOneofCase.FileChunk when e.FileChunk.TransferId == tid:
                    var d = e.FileChunk.Data.ToByteArray();
                    buffer.Write(d, 0, d.Length);
                    acks++;
                    _ = client.Router.PushAsync(new Envelope
                    {
                        FileAck = new FileAck { TransferId = tid, Offset = (ulong)buffer.Length },
                    });
                    break;

                case Envelope.BodyOneofCase.FileClose when e.FileClose.TransferId == tid:
                    expected = e.FileClose.Sha256.ToByteArray();
                    done.TrySetResult(true);
                    break;
            }
        }

        client.Router.OnMessage += OnMsg;
        try
        {
            var open = await client.Router.RequestAsync(new Envelope
            {
                FileOpen = new FileOpen { TransferId = tid, Direction = FileOpen.Types.Direction.Pull, Path = path },
            });
            Assert.Equal(Envelope.BodyOneofCase.FileOpenResult, open.BodyCase);

            await done.Task;
        }
        finally
        {
            client.Router.OnMessage -= OnMsg;
        }

        var got = buffer.ToArray();
        var actual = SHA256.HashData(got);
        bool ok = expected is not null && actual.AsSpan().SequenceEqual(expected);
        return (ok, got, acks);
    }

    private static byte[] RandomBytes(int n)
    {
        // Deterministic-enough filler; content specifics don't matter, only integrity.
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i * 31 + 7) & 0xFF);
        return b;
    }
}
