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

    // On READ, WTSVirtualChannelRead exposes an 8-byte CHANNEL_PDU_HEADER (length +
    // flags) that the client's DVC COM Write added — we strip it (see reassembler).
    // On WRITE, we send the frame RAW: the client's DVC COM OnDataReceived hands the
    // app exactly what we wrote, so a header here would desync the client.
    public const int ChannelPduHeaderSize = 8;

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

    /// <summary>Write a frame RAW (no CHANNEL_PDU_HEADER — the client's DVC layer delivers it as-is).</summary>
    public static bool WriteFrame(IntPtr h, byte[] frame)
    {
        int offset = 0;
        while (offset < frame.Length)
        {
            byte[] chunk = offset == 0 ? frame : frame[offset..];
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
