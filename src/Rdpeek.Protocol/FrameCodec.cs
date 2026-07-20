using System.Buffers.Binary;
using Dvc.Diag.Protocol;
using Google.Protobuf;

namespace Rdpeek.Protocol;

/// <summary>Raised when a frame violates the wire rules (oversize, bad prefix).</summary>
public sealed class FrameException : Exception
{
    public FrameException(string message) : base(message) { }
}

/// <summary>
/// Length-prefixed framing shared by the DVC channels and the local broker pipe:
/// <c>[4-byte little-endian uint32 length][payload]</c>. A single frame must not
/// exceed <see cref="MaxFrameSize"/> (16 MiB) — matches the Microsoft advanced sample.
/// </summary>
public static class Frame
{
    public const int MaxFrameSize = 16 * 1024 * 1024;

    /// <summary>Prefix a raw payload with its little-endian length.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxFrameSize)
            throw new FrameException($"payload {payload.Length} exceeds max frame size {MaxFrameSize}");

        var buf = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)payload.Length);
        payload.CopyTo(buf.AsSpan(4));
        return buf;
    }

    /// <summary>Serialize and frame an <see cref="Envelope"/>.</summary>
    public static byte[] Encode(Envelope envelope) => Encode(envelope.ToByteArray());
}

/// <summary>
/// Stateful, incremental frame reassembler. Feed it whatever bytes arrive from the
/// transport (any chunking); it returns the complete frames that became available.
/// Not thread-safe — one decoder per channel/connection, driven by that channel's
/// single reader.
/// </summary>
public sealed class FrameDecoder
{
    private readonly byte[] _lenBuf = new byte[4];
    private int _lenFilled;
    private byte[]? _payload;
    private int _payloadFilled;
    private int _need = -1; // -1 => still reading the length prefix

    /// <summary>
    /// Push received bytes; returns zero or more fully-assembled frame payloads.
    /// Throws <see cref="FrameException"/> if a declared length exceeds the cap.
    /// </summary>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data)
    {
        var frames = new List<byte[]>();
        int pos = 0;

        while (pos < data.Length)
        {
            if (_need < 0)
            {
                int take = Math.Min(4 - _lenFilled, data.Length - pos);
                data.Slice(pos, take).CopyTo(_lenBuf.AsSpan(_lenFilled));
                _lenFilled += take;
                pos += take;
                if (_lenFilled < 4) break; // need more bytes for the prefix

                uint len = BinaryPrimitives.ReadUInt32LittleEndian(_lenBuf);
                if (len > MaxFrameSizeGuard)
                    throw new FrameException($"declared frame length {len} exceeds max frame size {Frame.MaxFrameSize}");

                _need = (int)len;
                _payload = new byte[_need];
                _payloadFilled = 0;
                _lenFilled = 0;

                if (_need == 0) // zero-length frame is legal
                {
                    frames.Add(_payload);
                    _payload = null;
                    _need = -1;
                }
            }
            else
            {
                int take = Math.Min(_need - _payloadFilled, data.Length - pos);
                data.Slice(pos, take).CopyTo(_payload!.AsSpan(_payloadFilled));
                _payloadFilled += take;
                pos += take;
                if (_payloadFilled == _need)
                {
                    frames.Add(_payload!);
                    _payload = null;
                    _need = -1;
                }
            }
        }

        return frames;
    }

    /// <summary>Push bytes and parse each completed frame into an <see cref="Envelope"/>.</summary>
    public IReadOnlyList<Envelope> PushEnvelopes(ReadOnlySpan<byte> data)
    {
        var raw = Push(data);
        var result = new List<Envelope>(raw.Count);
        foreach (var frame in raw)
            result.Add(Envelope.Parser.ParseFrom(frame));
        return result;
    }

    // Compared as uint to avoid int overflow on a hostile 0xFFFFFFFF prefix.
    private const uint MaxFrameSizeGuard = Frame.MaxFrameSize;
}
