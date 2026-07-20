using Dvc.Diag.Protocol;
using Rdpeek.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// Opens the diagnostics DVC channel, serves the <see cref="AgentCore"/> over it, and
/// re-opens across disconnect/reconnect. This is the production path (needs a client
/// plugin listening on the same channel); the collectors it serves are the same ones
/// `selftest` exercises.
/// </summary>
internal static class ServeLoop
{
    private const string InspectorChannel = "dvc::diag::inspector";

    public static int Run()
    {
        Console.WriteLine($"rdpeek-agent: serving on '{InspectorChannel}' (Ctrl+C to stop)");
        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        while (!stop.IsSet)
        {
            IntPtr h = WtsChannel.Open(InspectorChannel);
            if (h == IntPtr.Zero)
            {
                // Client listener not present yet (no connection, or plugin not loaded). Retry.
                stop.Wait(1000);
                continue;
            }

            Console.WriteLine("channel open — client connected.");
            try { Serve(h, stop); }
            finally { WtsChannel.Close(h); }

            if (!stop.IsSet)
            {
                Console.WriteLine("channel closed — awaiting reconnect.");
                stop.Wait(1000);
            }
        }

        Console.WriteLine("rdpeek-agent: stopped.");
        return 0;
    }

    private static void Serve(IntPtr h, ManualResetEventSlim stop)
    {
        var decoder = new FrameDecoder();
        var router = new EnvelopeRouter(env =>
        {
            WtsChannel.WriteFrame(h, Frame.Encode(env));
            return Task.CompletedTask;
        });
        _ = new AgentCore(router);

        var buffer = new byte[64 * 1024];
        while (!stop.IsSet)
        {
            var (status, bytes) = WtsChannel.Read(h, buffer, timeoutMs: 2000);
            switch (status)
            {
                case WtsChannel.ReadStatus.Timeout:
                    continue;
                case WtsChannel.ReadStatus.Closed:
                    return; // disconnect — let Run re-open
                case WtsChannel.ReadStatus.NeedLargerBuffer:
                    buffer = new byte[Math.Max(bytes, buffer.Length * 2)];
                    continue;
                case WtsChannel.ReadStatus.Data:
                    foreach (var env in decoder.PushEnvelopes(buffer.AsSpan(0, bytes)))
                        router.Handle(env);
                    break;
            }
        }
    }
}
