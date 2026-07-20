using System.Security.Cryptography;
using Dvc.Diag.Protocol;
using Rdpeek.Protocol;
using Google.Protobuf;

namespace Conformance;

/// <summary>
/// A stand-in remote agent used to exercise the wire contract in-process. Handles
/// the handshake, sysinfo (one-shot + subscription), process list, and a windowed
/// file pull. Fault knobs let tests induce corruption to prove integrity checking.
/// </summary>
public sealed class MockAgent
{
    private readonly EnvelopeRouter _router;

    /// <summary>Virtual files available for PULL, keyed by path.</summary>
    public Dictionary<string, byte[]> Files { get; } = new();

    /// <summary>How many snapshots a subscription (interval &gt; 0) emits.</summary>
    public int SubscriptionPushes { get; set; } = 3;

    /// <summary>Chunk size for file pulls.</summary>
    public int ChunkSize { get; set; } = 4096;

    /// <summary>If &ge; 0, flip a byte in that chunk index during a pull (fault injection).</summary>
    public int CorruptChunkIndex { get; set; } = -1;

    public MockAgent(DiagEndpoint endpoint)
    {
        _router = endpoint.Router;
        _router.OnMessage += Handle;
    }

    private void Handle(Envelope env)
    {
        switch (env.BodyCase)
        {
            case Envelope.BodyOneofCase.Hello:
                _ = _router.RespondAsync(new Envelope { Capabilities = MakeCapabilities() }, env.RequestId);
                break;
            case Envelope.BodyOneofCase.SysinfoRequest:
                HandleSysInfo(env);
                break;
            case Envelope.BodyOneofCase.ProcessListRequest:
                _ = _router.RespondAsync(new Envelope { ProcessList = MakeProcessList() }, env.RequestId);
                break;
            case Envelope.BodyOneofCase.FileOpen:
                HandlePull(env);
                break;
        }
    }

    private Capabilities MakeCapabilities() => new()
    {
        ProtocolVersion = 1,
        AgentBuild = "mock-agent/1.0",
        Sysinfo = true,
        ProcessList = true,
        FilePull = true,
        MaxChunkBytes = (uint)ChunkSize,
        FileRoots = { "C:\\diag" },
    };

    private static SysInfoSnapshot MakeSnapshot() => new()
    {
        OsBuild = 26200,
        OsUbr = 1234,
        OsDisplayVer = "24H2",
        OsProductName = "Windows 11 Pro",
        UptimeMs = 3_600_000,
        CpuName = "Mock CPU",
        CpuLogical = 8,
        CpuPercent = 12.5,
        MemTotalBytes = 16UL * 1024 * 1024 * 1024,
        HostName = "REMOTE01",
        UserName = "tester",
        SessionId = 1,
        ClientName = "CLIENT01",
        Protocol = "RDP",
    };

    private void HandleSysInfo(Envelope env)
    {
        var interval = env.SysinfoRequest.IntervalMs;
        if (interval == 0)
        {
            _ = _router.RespondAsync(new Envelope { SysinfoSnapshot = MakeSnapshot() }, env.RequestId);
            return;
        }

        // Subscription: emit N unsolicited snapshots (request_id == 0).
        for (int i = 0; i < SubscriptionPushes; i++)
            _ = _router.PushAsync(new Envelope { SysinfoSnapshot = MakeSnapshot() });
    }

    private static ProcessList MakeProcessList() => new()
    {
        Processes =
        {
            new ProcessList.Types.Proc { Pid = 4232, SessionId = 1, ImageName = "explorer.exe", UserName = "tester", WorkingSet = 50_000_000 },
            new ProcessList.Types.Proc { Pid = 9001, SessionId = 1, ImageName = "notepad.exe",  UserName = "tester", WorkingSet = 12_000_000 },
        },
    };

    private void HandlePull(Envelope env)
    {
        var open = env.FileOpen;
        if (!Files.TryGetValue(open.Path, out var content))
        {
            _ = _router.RespondAsync(
                new Envelope { Error = new Error { Code = Error.Types.Code.NotFound, Message = open.Path } },
                env.RequestId);
            return;
        }

        var digest = SHA256.HashData(content); // over the ORIGINAL bytes
        _ = _router.RespondAsync(
            new Envelope { FileOpenResult = new FileOpenResult { TransferId = open.TransferId, TotalSize = (ulong)content.Length } },
            env.RequestId);

        int idx = 0;
        for (int off = 0; off < content.Length || (content.Length == 0 && idx == 0); off += ChunkSize)
        {
            int len = Math.Min(ChunkSize, content.Length - off);
            if (len < 0) len = 0;
            var slice = new byte[len];
            Array.Copy(content, off, slice, 0, len);
            if (idx == CorruptChunkIndex && len > 0) slice[0] ^= 0xFF; // induce corruption

            bool last = off + len >= content.Length;
            _ = _router.PushAsync(new Envelope
            {
                FileChunk = new FileChunk
                {
                    TransferId = open.TransferId,
                    Offset = (ulong)off,
                    Data = ByteString.CopyFrom(slice),
                    Last = last,
                },
            });
            idx++;
            if (content.Length == 0) break;
        }

        _ = _router.PushAsync(new Envelope
        {
            FileClose = new FileClose
            {
                TransferId = open.TransferId,
                Status = FileClose.Types.Status.Complete,
                Sha256 = ByteString.CopyFrom(digest),
            },
        });
    }
}
