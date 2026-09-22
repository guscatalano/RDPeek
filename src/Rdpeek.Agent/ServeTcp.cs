using System.Net;
using System.Net.Sockets;
using Rdpeek.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// Serves the real <see cref="AgentCore"/> over a TCP socket instead of the WTS virtual channel.
/// The transport is the only thing that changes — same collectors, same file service — so a mock RDP
/// server can bridge its diagnostics DVC to this port and drive the genuine agent without
/// reimplementing the protocol. Frames are the same <c>[4-byte LE len][Envelope]</c> as on the wire.
/// </summary>
internal static class ServeTcp
{
    public static int Run(int port, IReadOnlyList<string> fileRoots)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Logger.Log($"serve-tcp start on 127.0.0.1:{port} (roots: {string.Join(", ", fileRoots)})");
        Console.WriteLine($"rdpeek-agent: serving on tcp 127.0.0.1:{port} (Ctrl+C to stop)");

        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); listener.Stop(); };

        while (!stop.IsSet)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { break; }   // listener stopped

            Console.WriteLine("tcp client connected (bridge).");
            Logger.Log("serve-tcp: client connected.");
            try { Serve(client, fileRoots, stop); }
            catch (Exception ex) { Logger.Log($"serve-tcp error: {ex}"); }
            finally { client.Dispose(); }

            if (!stop.IsSet) Logger.Log("serve-tcp: client gone — awaiting next.");
        }
        return 0;
    }

    private static void Serve(TcpClient client, IReadOnlyList<string> fileRoots, ManualResetEventSlim stop)
    {
        using var stream = client.GetStream();
        var writeLock = new object();
        var decoder = new FrameDecoder();
        var router = new EnvelopeRouter(env =>
        {
            var frame = Frame.Encode(env);
            lock (writeLock) { stream.Write(frame, 0, frame.Length); stream.Flush(); }
            return Task.CompletedTask;
        });
        _ = new AgentCore(router, fileRoots);

        var buf = new byte[64 * 1024];
        while (!stop.IsSet)
        {
            int n;
            try { n = stream.Read(buf, 0, buf.Length); }
            catch { return; }
            if (n <= 0) return;   // peer closed
            try
            {
                foreach (var env in decoder.PushEnvelopes(buf.AsSpan(0, n)))
                    router.Handle(env);
            }
            catch (Exception ex) { Logger.Log($"serve-tcp decode error: {ex.Message}"); }
        }
    }
}
