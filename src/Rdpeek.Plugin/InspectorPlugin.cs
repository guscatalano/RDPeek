using System.Runtime.InteropServices;
using Rdpeek.Client;

namespace Rdpeek.Plugin;

/// <summary>
/// The COM object the RDP client instantiates (IWTSPlugin). On Initialize it creates
/// a listener for the diagnostics channel; the rest of the work happens per-connection
/// in <see cref="ChannelCallback"/>.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid(PluginHost.ClsidString)]
internal sealed class InspectorPlugin : IWTSPlugin
{
    internal const string InspectorChannel = "dvc::diag::inspector";

    // Per-connection identity for the companion broker (pid + seq).
    private static int _seqCounter;
    private readonly int _seq = System.Threading.Interlocked.Increment(ref _seqCounter);

    private IWTSVirtualChannelManager? _manager;
    private IWTSListener? _listener;

    private bool _terminated;

    public int Initialize(IWTSVirtualChannelManager pChannelMgr)
    {
        Logger.Log("IWTSPlugin.Initialize");
        PluginHost.PluginActivated();   // keep the shared server alive while this connection lives
        _manager = pChannelMgr;

        // Initialize is a per-connection COM call marshaled from the RDP client (mstsc/msrdc). Grab the
        // caller's process id here — that's the client window's pid, which the companion's switcher keys
        // on (WindowPid). Reporting it lets the companion join THIS agent to THIS window exactly, even
        // through a gateway/Cloud PC where host names never match. 0 if it can't be determined.
        int clientPid = CallerProcessId();
        Logger.Log($"caller (RDP client) pid={clientPid}");
        if (clientPid != 0) Broker.Report("clientpid", Environment.ProcessId, _seq, clientPid.ToString());

        int hr = pChannelMgr.CreateListener(InspectorChannel, 0, new ListenerCallback(_seq), out var listener);
        Logger.Log($"CreateListener('{InspectorChannel}') hr=0x{hr:X8}");
        if (hr >= 0) _listener = listener;

        // Client-side view: how DVCs are configured on this machine. Logged here (at
        // load) so it appears even when no agent is serving on the remote side.
        Logger.Log(ClientChannels.Format(ClientChannels.Collect()));

        // Tell the companion this connection is up and awaiting an agent.
        Broker.Report("listening", Environment.ProcessId, _seq);
        return hr >= 0 ? 0 : hr;
    }

    // ── caller PID via COM ────────────────────────────────────────────────────
    // During an incoming out-of-proc COM call, CoGetCallerTID yields the caller's thread id (S_OK only
    // when the caller is a different process, which mstsc/msrdc always is); the thread → its process.
    private const int THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    [DllImport("ole32.dll")] private static extern int CoGetCallerTID(out uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(int access, bool inherit, uint tid);
    [DllImport("kernel32.dll")] private static extern uint GetProcessIdOfThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private static int CallerProcessId()
    {
        try
        {
            if (CoGetCallerTID(out uint tid) == 0 && tid != 0)   // S_OK == cross-process caller
            {
                IntPtr h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, tid);
                if (h != IntPtr.Zero)
                    try { return (int)GetProcessIdOfThread(h); } finally { CloseHandle(h); }
            }
        }
        catch { }
        return 0;
    }

    public int Connected()
    {
        Logger.Log("IWTSPlugin.Connected");
        return 0;
    }

    public int Disconnected(int dwDisconnectCode)
    {
        Logger.Log($"IWTSPlugin.Disconnected code={dwDisconnectCode}");
        Broker.Report("gone", Environment.ProcessId, _seq);
        return 0;
    }

    public int Terminated()
    {
        Logger.Log("IWTSPlugin.Terminated");
        if (_terminated) return 0;   // guard against a double callback skewing the ref count
        _terminated = true;
        Broker.Report("gone", Environment.ProcessId, _seq);
        // Only the LAST live connection shutting down exits the shared server — otherwise ending one
        // session would kill the plugin for every other still-open connection.
        PluginHost.PluginTerminated();
        return 0;
    }
}
