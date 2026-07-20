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

    /// <summary>Write a full frame, looping until all bytes are accepted.</summary>
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
