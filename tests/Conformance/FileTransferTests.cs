using System.Security.Cryptography;
using Dvc.Diag.Protocol;
using Rdpeek.Protocol;
using Xunit;

namespace Conformance;

/// <summary>
/// End-to-end file PULL over the in-proc loopback using the real reusable transfer classes
/// (<see cref="FilePullService"/> on the agent side, <see cref="FilePullReceiver"/> on the client),
/// exercising windowing, sha256 integrity, empty files, and the path-root security boundary.
/// </summary>
public class FileTransferTests
{
    /// <summary>In-memory <see cref="IFileSource"/>: a virtual "C:\diag" root over a dictionary.</summary>
    private sealed class MemoryFileSource : IFileSource
    {
        public const string Root = @"C:\diag";
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FileSourceResult OpenRead(string path, out Stream? stream, out long size)
        {
            stream = null; size = 0;
            if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return FileSourceResult.NotAllowed;
            if (!Files.TryGetValue(path, out var b)) return FileSourceResult.NotFound;
            stream = new MemoryStream(b, writable: false);
            size = b.Length;
            return FileSourceResult.Ok;
        }

        public FileSourceResult List(string path, out FileList? list)
        {
            list = null;
            if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return FileSourceResult.NotAllowed;
            var fl = new FileList { Path = path };
            foreach (var (k, v) in Files)
                if (k.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    fl.Items.Add(new FileList.Types.Item { Name = System.IO.Path.GetFileName(k), Size = (ulong)v.Length });
            list = fl;
            return FileSourceResult.Ok;
        }
    }

    private static (LoopbackLink link, MemoryFileSource src, FilePullService svc) Setup(int chunk = 4096, int window = 2)
    {
        var link = new LoopbackLink();
        var src = new MemoryFileSource();
        var svc = new FilePullService(link.Agent.Router, src, chunkSize: chunk, windowChunks: window);
        return (link, src, svc);
    }

    [Fact]
    public async Task Pull_transfers_bytes_and_verifies_sha256()
    {
        var (link, src, svc) = Setup();
        using (svc)
        {
            var content = RandomBytes(10_000);
            src.Files[@"C:\diag\app.log"] = content;

            var dest = new MemoryStream();
            var result = await new FilePullReceiver(link.Client.Router, 1, dest).RunAsync(@"C:\diag\app.log");

            Assert.True(result.Ok);
            Assert.Equal(content, dest.ToArray());
            Assert.Equal(content.Length, result.Bytes);
        }
    }

    [Fact]
    public async Task Pull_large_file_respects_window_and_completes()
    {
        // Small chunk + small window forces many ack-gated round trips; must not deadlock.
        var (link, src, svc) = Setup(chunk: 1024, window: 2);
        using (svc)
        {
            var content = RandomBytes(200_000);
            src.Files[@"C:\diag\big.bin"] = content;

            var dest = new MemoryStream();
            var result = await new FilePullReceiver(link.Client.Router, 7, dest).RunAsync(@"C:\diag\big.bin");

            Assert.True(result.Ok);
            Assert.Equal(SHA256.HashData(content), SHA256.HashData(dest.ToArray()));
        }
    }

    [Fact]
    public async Task Pull_empty_file_completes()
    {
        var (link, src, svc) = Setup();
        using (svc)
        {
            src.Files[@"C:\diag\empty.txt"] = Array.Empty<byte>();
            var dest = new MemoryStream();
            var result = await new FilePullReceiver(link.Client.Router, 2, dest).RunAsync(@"C:\diag\empty.txt");
            Assert.True(result.Ok);
            Assert.Equal(0, result.Bytes);
        }
    }

    [Fact]
    public async Task Pull_missing_file_reports_not_found()
    {
        var (link, src, svc) = Setup();
        using (svc)
        {
            var result = await new FilePullReceiver(link.Client.Router, 3, new MemoryStream()).RunAsync(@"C:\diag\nope.log");
            Assert.False(result.Ok);
            Assert.Contains("nope", result.Message);
        }
    }

    [Fact]
    public async Task Pull_outside_root_is_rejected()
    {
        var (link, src, svc) = Setup();
        using (svc)
        {
            var reply = await link.Client.Router.RequestAsync(new Envelope
            {
                FileOpen = new FileOpen { TransferId = 4, Direction = FileOpen.Types.Direction.Pull, Path = @"C:\Windows\System32\config\SAM" },
            });
            Assert.Equal(Envelope.BodyOneofCase.Error, reply.BodyCase);
            Assert.Equal(Error.Types.Code.PathNotAllowed, reply.Error.Code);
        }
    }

    [Fact]
    public async Task Push_is_rejected_on_readonly_build()
    {
        var (link, src, svc) = Setup();
        using (svc)
        {
            var reply = await link.Client.Router.RequestAsync(new Envelope
            {
                FileOpen = new FileOpen { TransferId = 5, Direction = FileOpen.Types.Direction.Push, Path = @"C:\diag\x", TotalSize = 1 },
            });
            Assert.Equal(Envelope.BodyOneofCase.Error, reply.BodyCase);
            Assert.Equal(Error.Types.Code.NotSupported, reply.Error.Code);
        }
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i * 31 + 7) & 0xFF);
        return b;
    }
}
