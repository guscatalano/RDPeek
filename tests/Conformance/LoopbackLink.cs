using Dvc.Diag.Protocol;
using Rdpeek.Protocol;

namespace Conformance;

/// <summary>
/// One end of an in-process duplex link: a router plus its own frame decoder.
/// Sending encodes through <see cref="Frame"/> and hands the bytes to the peer's
/// decoder — exactly the path a real DVC/pipe transport takes, minus the wire.
/// </summary>
public sealed class DiagEndpoint
{
    private readonly FrameDecoder _decoder = new();
    private Func<byte[], Task> _peerDeliver = _ => Task.CompletedTask;

    public EnvelopeRouter Router { get; }

    public DiagEndpoint()
        => Router = new EnvelopeRouter(env => _peerDeliver(Frame.Encode(env)));

    internal void SetPeer(DiagEndpoint peer) => _peerDeliver = peer.DeliverAsync;

    /// <summary>Receive framed bytes from the peer: decode and route each envelope.</summary>
    public Task DeliverAsync(byte[] frameBytes)
    {
        foreach (var env in _decoder.PushEnvelopes(frameBytes))
            Router.Handle(env);
        return Task.CompletedTask;
    }
}

/// <summary>A back-to-back pair of endpoints — the whole codec/router stack, no RDP.</summary>
public sealed class LoopbackLink
{
    public DiagEndpoint Client { get; } = new();
    public DiagEndpoint Agent { get; } = new();

    public LoopbackLink()
    {
        Client.SetPeer(Agent);
        Agent.SetPeer(Client);
    }
}
