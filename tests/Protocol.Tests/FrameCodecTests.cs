using System.Buffers.Binary;
using Dvc.Diag.Protocol;
using Rdpeek.Protocol;
using Xunit;

namespace Protocol.Tests;

public class FrameCodecTests
{
    private static Envelope SamplePing(ulong seq) =>
        new() { Ping = new Ping { SequenceNumber = seq } };

    [Fact]
    public void Encode_then_decode_roundtrips()
    {
        var env = SamplePing(42);
        var frame = Frame.Encode(env);

        var decoder = new FrameDecoder();
        var got = decoder.PushEnvelopes(frame);

        Assert.Single(got);
        Assert.Equal(42ul, got[0].Ping.SequenceNumber);
    }

    [Fact]
    public void Decode_reassembles_across_byte_by_byte_pushes()
    {
        var frame = Frame.Encode(SamplePing(7));
        var decoder = new FrameDecoder();

        Envelope? result = null;
        for (int i = 0; i < frame.Length; i++)
        {
            var got = decoder.PushEnvelopes(frame.AsSpan(i, 1));
            if (got.Count > 0) result = got[0];
        }

        Assert.NotNull(result);
        Assert.Equal(7ul, result!.Ping.SequenceNumber);
    }

    [Fact]
    public void Decode_handles_multiple_frames_in_one_push()
    {
        var a = Frame.Encode(SamplePing(1));
        var b = Frame.Encode(SamplePing(2));
        var c = Frame.Encode(SamplePing(3));
        var combined = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(combined, 0);
        b.CopyTo(combined, a.Length);
        c.CopyTo(combined, a.Length + b.Length);

        var got = new FrameDecoder().PushEnvelopes(combined);

        Assert.Equal(3, got.Count);
        Assert.Equal(new ulong[] { 1, 2, 3 }, got.Select(e => e.Ping.SequenceNumber));
    }

    [Fact]
    public void Decode_splits_frame_boundary_across_two_pushes()
    {
        var a = Frame.Encode(SamplePing(10));
        var b = Frame.Encode(SamplePing(20));
        var combined = new byte[a.Length + b.Length];
        a.CopyTo(combined, 0);
        b.CopyTo(combined, a.Length);

        var decoder = new FrameDecoder();
        // Split mid-way through the second frame's payload.
        int split = a.Length + 3;
        var first = decoder.PushEnvelopes(combined.AsSpan(0, split));
        var second = decoder.PushEnvelopes(combined.AsSpan(split));

        Assert.Single(first);
        Assert.Equal(10ul, first[0].Ping.SequenceNumber);
        Assert.Single(second);
        Assert.Equal(20ul, second[0].Ping.SequenceNumber);
    }

    [Fact]
    public void Zero_length_frame_is_emitted()
    {
        var frame = Frame.Encode(ReadOnlySpan<byte>.Empty);
        Assert.Equal(4, frame.Length); // just the prefix

        var got = new FrameDecoder().Push(frame);
        Assert.Single(got);
        Assert.Empty(got[0]);
    }

    [Fact]
    public void Encode_rejects_oversize_payload()
    {
        var big = new byte[Frame.MaxFrameSize + 1];
        Assert.Throws<FrameException>(() => Frame.Encode(big));
    }

    [Fact]
    public void Decode_rejects_oversize_length_prefix()
    {
        // Hostile prefix declaring 0xFFFFFFFF bytes — must be rejected, not allocated.
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, uint.MaxValue);

        Assert.Throws<FrameException>(() => new FrameDecoder().Push(prefix));
    }
}
