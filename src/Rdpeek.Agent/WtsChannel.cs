using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Rdpeek.Agent;

/// <summary>
/// Server-side (in-session) dynamic virtual channel: open/read/write/close over the
/// WTS API. The client plugin must be listening on the same channel name for the
/// open to succeed.
/// </summary>
internal static class WtsChannel
{
    private const uint WTS_CHANNEL_OPTION_DYNAMIC = 0x00000001;
    private const uint WTS_CURRENT_SESSION = 0xFFFFFFFF;

    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_IO_INCOMPLETE = 996;

    // Virtual-channel chunking (MS-RDPBCGR CHANNEL_PDU_HEADER: length + flags).
    // The WTS API exposes this; the client's DVC COM API hides it. We must strip it
    // on read and add it on write.
    public const int ChannelPduHeaderSize = 8;
    private const uint CHANNEL_FLAG_FIRST = 0x01;
    private const uint CHANNEL_FLAG_LAST = 0x02;
    private const int CHANNEL_CHUNK_LENGTH = 1600;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr WTSVirtualChannelOpenEx(uint sessionId, string pVirtualName, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSVirtualChannelRead(IntPtr hChannel, uint timeoutMs, byte[] buffer, uint bufferSize, out uint pBytesRead);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSVirtualChannelWrite(IntPtr hChannel, byte[] buffer, uint length, out uint pBytesWritten);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSVirtualChannelClose(IntPtr hChannel);

    public enum ReadStatus { Data, Timeout, Closed, NeedLargerBuffer }

    /// <summary>Open the named dynamic channel in the current session. IntPtr.Zero on failure.</summary>
    public static IntPtr Open(string channelName) =>
        WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, channelName, WTS_CHANNEL_OPTION_DYNAMIC);

    public static void Close(IntPtr h)
    {
        if (h != IntPtr.Zero) WTSVirtualChannelClose(h);
    }

    /// <summary>
    /// Read one channel message. On <see cref="ReadStatus.NeedLargerBuffer"/> the
    /// returned count is the required buffer size; the data stays queued for a retry.
    /// </summary>
    public static (ReadStatus status, int bytes) Read(IntPtr h, byte[] buffer, uint timeoutMs)
    {
        if (WTSVirtualChannelRead(h, timeoutMs, buffer, (uint)buffer.Length, out uint read))
            return (ReadStatus.Data, (int)read);

        int err = Marshal.GetLastWin32Error();
        return err switch
        {
            ERROR_INSUFFICIENT_BUFFER => (ReadStatus.NeedLargerBuffer, (int)read),
            ERROR_IO_INCOMPLETE or 0 => (ReadStatus.Timeout, 0),
            _ => (ReadStatus.Closed, 0),
        };
    }

    /// <summary>
    /// Write a message wrapped in CHANNEL_PDU_HEADER chunk(s) — the framing the client's
    /// DVC layer expects and strips. Small messages go in a single FIRST|LAST chunk.
    /// </summary>
    public static bool WriteMessage(IntPtr h, byte[] data)
    {
        int offset = 0;
        do
        {
            int chunk = Math.Min(CHANNEL_CHUNK_LENGTH, data.Length - offset);
            uint flags = 0;
            if (offset == 0) flags |= CHANNEL_FLAG_FIRST;
            if (offset + chunk >= data.Length) flags |= CHANNEL_FLAG_LAST;

            var pdu = new byte[ChannelPduHeaderSize + chunk];
            BinaryPrimitives.WriteUInt32LittleEndian(pdu, (uint)data.Length); // total message length
            BinaryPrimitives.WriteUInt32LittleEndian(pdu.AsSpan(4), flags);
            Array.Copy(data, offset, pdu, ChannelPduHeaderSize, chunk);

            if (!WriteRaw(h, pdu)) return false;
            offset += chunk;
        }
        while (offset < data.Length);
        return true;
    }

    private static bool WriteRaw(IntPtr h, byte[] bytes)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            byte[] chunk = offset == 0 ? bytes : bytes[offset..];
            if (!WTSVirtualChannelWrite(h, chunk, (uint)chunk.Length, out uint written) || written == 0)
                return false;
            offset += (int)written;
        }
        return true;
    }
}

/// <summary>
/// Reassembles CHANNEL_PDU_HEADER-chunked channel data (from WTSVirtualChannelRead)
/// back into complete messages using the FIRST/LAST flags.
/// </summary>
internal sealed class ChannelPduReassembler
{
    private readonly List<byte> _buffer = new();

    /// <summary>Feed one raw read; returns a complete message when a LAST chunk arrives.</summary>
    public byte[]? Push(byte[] raw, int count)
    {
        if (count < WtsChannel.ChannelPduHeaderSize) return null; // malformed / too small

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
        if ((flags & 0x01) != 0) _buffer.Clear();                 // CHANNEL_FLAG_FIRST

        for (int i = WtsChannel.ChannelPduHeaderSize; i < count; i++)
            _buffer.Add(raw[i]);

        if ((flags & 0x02) != 0)                                  // CHANNEL_FLAG_LAST
        {
            var message = _buffer.ToArray();
            _buffer.Clear();
            return message;
        }
        return null;
    }
}
