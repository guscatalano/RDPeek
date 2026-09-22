using Dvc.Diag.Protocol;
using Google.Protobuf;
using Rdpeek.Protocol;
using Xunit;

namespace Protocol.Tests;

public class FrameInspectorTests
{
    private static byte[] FrameOf(Envelope env) => Frame.Encode(env);

    [Fact]
    public void Decodes_frame_metadata_and_field_tree()
    {
        var insp = new FrameInspector();
        var env = new Envelope
        {
            RequestId = 42,
            Hello = new Hello { ProtocolVersion = 1, ClientBuild = "viewer/test" },
        };

        var recs = insp.Push("out", FrameOf(env));
        var r = Assert.Single(recs);

        Assert.True(r.Decoded);
        Assert.Empty(r.Anomalies);
        Assert.Equal(42ul, r.RequestId);
        Assert.Equal("Hello", r.BodyCase);
        Assert.Equal("out", r.Direction);
        // Field tree: request_id + the hello message with its populated fields.
        Assert.Contains(r.Fields, f => f.Name == "request_id" && f.Value == "42");
        var hello = Assert.Single(r.Fields, f => f.Name == "hello");
        Assert.Contains(hello.Children, c => c.Name == "client_build" && c.Value == "viewer/test");
    }

    [Fact]
    public void Flags_oversized_length_prefix()
    {
        var insp = new FrameInspector();
        // A length prefix well over the 16 MiB cap, no payload.
        var bad = new byte[] { 0xFF, 0xFF, 0xFF, 0x7F };
        var r = Assert.Single(insp.Push("in", bad));
        Assert.False(r.Decoded);
        Assert.Contains(r.Anomalies, a => a.Contains("cap"));
    }

    [Fact]
    public void Flags_undecodable_payload()
    {
        var insp = new FrameInspector();
        // Valid framing (length 3) but the payload isn't a valid Envelope.
        var frame = new byte[] { 3, 0, 0, 0, 0xFF, 0xFF, 0xFF };
        var r = Assert.Single(insp.Push("in", frame));
        Assert.False(r.Decoded);
        Assert.Contains(r.Anomalies, a => a.Contains("decode failed"));
    }

    [Fact]
    public void Reassembles_a_frame_split_across_pushes()
    {
        var insp = new FrameInspector();
        var frame = FrameOf(new Envelope { RequestId = 7, Ping = new Ping { SequenceNumber = 3 } });

        Assert.Empty(insp.Push("out", frame.AsSpan(0, 2)));   // half the length prefix
        Assert.Empty(insp.Push("out", frame.AsSpan(2, 3)));   // rest of prefix + part of body
        var recs = insp.Push("out", frame.AsSpan(5));         // the tail completes it
        var r = Assert.Single(recs);
        Assert.True(r.Decoded);
        Assert.Equal("Ping", r.BodyCase);
        Assert.Equal(7ul, r.RequestId);
    }

    [Fact]
    public void Two_frames_in_one_push_yield_two_records()
    {
        var insp = new FrameInspector();
        var a = FrameOf(new Envelope { RequestId = 1, Ping = new Ping() });
        var b = FrameOf(new Envelope { RequestId = 2, Ping = new Ping() });
        var recs = insp.Push("in", a.Concat(b).ToArray());
        Assert.Equal(2, recs.Count);
        Assert.Equal(1ul, recs[0].RequestId);
        Assert.Equal(2ul, recs[1].RequestId);
    }
}
