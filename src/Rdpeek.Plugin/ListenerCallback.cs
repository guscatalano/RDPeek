using System.Runtime.InteropServices;

namespace Rdpeek.Plugin;

/// <summary>Accepts new channel connections and hands back a per-connection callback.</summary>
[ComVisible(true)]
internal sealed class ListenerCallback : IWTSListenerCallback
{
    private readonly int _seq;

    public ListenerCallback(int seq) => _seq = seq;

    public int OnNewChannelConnection(
        IWTSVirtualChannel pChannel,
        string? data,
        out bool pbAccept,
        out IWTSVirtualChannelCallback ppCallback)
    {
        Logger.Log("OnNewChannelConnection — accepting");
        pbAccept = true;
        ppCallback = new ChannelCallback(pChannel, _seq);
        return 0; // S_OK
    }
}
