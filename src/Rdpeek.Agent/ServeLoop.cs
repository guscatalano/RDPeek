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
    private static IReadOnlyList<string> _fileRoots = Array.Empty<string>();
    private static bool _allowShell;
    private static bool _allowTrace;
    private static int _updateTriggered;

    /// <summary>A client connected and advertised its version. If it's newer than this agent, pull the
    /// latest release and re-exec — the running session updates itself. Once per process; best-effort.</summary>
    private static void OnClientVersion(string clientVersion)
    {
        if (Interlocked.Exchange(ref _updateTriggered, 1) != 0) return;
        if (!SelfUpdate.ClientIsNewer(clientVersion)) { Interlocked.Exchange(ref _updateTriggered, 0); return; }
        Logger.Log($"client v{clientVersion} is newer than this agent — updating");
        Console.WriteLine($"[update] client v{clientVersion} is newer — updating agent…");
        if (SelfUpdate.MaybeUpdateAndReexec(Environment.GetCommandLineArgs().Skip(1).ToArray()))
            Environment.Exit(0);   // new agent launched; drop this one so it takes over the channel
    }

    public static int Run(IReadOnlyList<string> fileRoots, bool allowShell = false, bool allowTrace = false)
    {
        _fileRoots = fileRoots;
        _allowShell = allowShell;
        _allowTrace = allowTrace;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Log($"UNHANDLED: {e.ExceptionObject}");

        Logger.Log($"serve start (pid {Environment.ProcessId}, session {Environment.GetEnvironmentVariable("SESSIONNAME")})");
        var ver = (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version) is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";
        Console.WriteLine($"rdpeek-agent v{ver} — serving on '{InspectorChannel}' in session {Environment.GetEnvironmentVariable("SESSIONNAME")} (Ctrl+C to stop)");
        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        var waitingSince = DateTime.UtcNow;
        int lastReport = -1;
        while (!stop.IsSet)
        {
            IntPtr h = WtsChannel.Open(InspectorChannel);
            if (h == IntPtr.Zero)
            {
                // No client-side listener yet: either no RDP session, or the RDPeek client plugin isn't
                // loaded on this connection. Report periodically so the window shows a live status and
                // the cause is obvious (rather than just sitting on "serving").
                int secs = (int)(DateTime.UtcNow - waitingSince).TotalSeconds;
                if (secs / 5 != lastReport)
                {
                    lastReport = secs / 5;
                    Console.WriteLine($"waiting for the RDP client to accept '{InspectorChannel}' … {secs}s" +
                        (secs >= 15 ? "  (no client plugin? make sure RDPeek is installed on the CLIENT and reconnect the RDP session)" : ""));
                }
                stop.Wait(1000);
                continue;
            }

            Logger.Log("channel open — client connected.");
            Console.WriteLine($"[connected] client attached on '{InspectorChannel}' — serving.");
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
                Console.WriteLine("[disconnected] client detached — waiting for it to come back…");
                waitingSince = DateTime.UtcNow;
                lastReport = -1;
                stop.Wait(1000);
            }
        }

        Logger.Log("serve stopped.");
        return 0;
    }

    private static void Serve(IntPtr h, ManualResetEventSlim stop)
    {
        var reassembler = new ChannelPduReassembler();
        var decoder = new FrameDecoder();
        var router = new EnvelopeRouter(env =>
        {
            WtsChannel.WriteFrame(h, Frame.Encode(env)); // raw — client's DVC layer delivers as-is
            return Task.CompletedTask;
        });
        _ = new AgentCore(router, _fileRoots, allowShell: _allowShell, allowTrace: _allowTrace, onClientVersion: OnClientVersion);

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
                    // Strip CHANNEL_PDU_HEADER chunks → complete message → our frame → Envelope.
                    var message = reassembler.Push(buffer, bytes);
                    if (message is null) break;
                    try
                    {
                        foreach (var env in decoder.PushEnvelopes(message))
                            router.Handle(env);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"decode error: {ex.Message}");
                    }
                    break;
            }
        }
    }
}
