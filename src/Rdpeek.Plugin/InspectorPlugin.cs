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
    private const string InspectorChannel = "dvc::diag::inspector";

    // Per-connection identity for the companion broker (pid + seq).
    private static int _seqCounter;
    private readonly int _seq = System.Threading.Interlocked.Increment(ref _seqCounter);

    private IWTSVirtualChannelManager? _manager;
    private IWTSListener? _listener;

    public int Initialize(IWTSVirtualChannelManager pChannelMgr)
    {
        Logger.Log("IWTSPlugin.Initialize");
        _manager = pChannelMgr;
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
        Broker.Report("gone", Environment.ProcessId, _seq);
        PluginHost.Shutdown.Set();
        return 0;
    }
}
