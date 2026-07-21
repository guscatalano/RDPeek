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
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Log($"UNHANDLED: {e.ExceptionObject}");

        Logger.Log($"serve start (pid {Environment.ProcessId}, session {Environment.GetEnvironmentVariable("SESSIONNAME")})");
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

            Logger.Log("channel open — client connected.");
            Console.WriteLine("channel open — client connected.");
            try
            {
                Serve(h, stop);
            }
            catch (Exception ex)
            {
                Logger.Log($"serve loop error: {ex}");
            }
            finally
            {
                WtsChannel.Close(h);
            }

            if (!stop.IsSet)
            {
                Logger.Log("channel closed — awaiting reconnect.");
                stop.Wait(1000);
            }
        }

        Logger.Log("serve stopped.");
        return 0;
    }

    private static void Serve(IntPtr h, ManualResetEventSlim stop)
    {
        var decoder = new FrameDecoder();
        var router = new EnvelopeRouter(env =>
        {
            var frame = Frame.Encode(env);
            Logger.Log($"WRITE {frame.Length}B: {Hex(frame, 48)}");
            WtsChannel.WriteFrame(h, frame);
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
                    Logger.Log($"read needs larger buffer: {bytes}");
                    buffer = new byte[Math.Max(bytes, buffer.Length * 2)];
                    continue;
                case WtsChannel.ReadStatus.Data:
                    Logger.Log($"READ  {bytes}B: {Hex(buffer, Math.Min(bytes, 48))}");
                    try
                    {
                        foreach (var env in decoder.PushEnvelopes(buffer.AsSpan(0, bytes)))
                        {
                            Logger.Log($"  decoded {env.BodyCase}");
                            router.Handle(env);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"decode error: {ex.Message}");
                    }
                    break;
            }
        }
    }

    private static string Hex(byte[] buf, int count)
    {
        var chars = new System.Text.StringBuilder(count * 3);
        for (int i = 0; i < count; i++) chars.Append(buf[i].ToString("X2")).Append(' ');
        return chars.ToString().TrimEnd();
    }
}
