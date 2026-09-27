using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// Grabs a downscaled screenshot of a window from the screen (GDI StretchBlt). Used for the switcher's
/// per-session thumbnails: a fullscreen RDP session you're not on is minimized (no live pixels), so we
/// capture it while it's the visible foreground session — i.e. right after switching to it.
/// </summary>
internal static class ScreenCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPPM, biYPPM; public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd, IntPtr src, int xs, int ys, int ws, int hs, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    private const uint SRCCOPY = 0x00CC0020;
    private const int HALFTONE = 4;

    public readonly record struct Shot(byte[] Bgra, int Width, int Height);

    /// <summary>Capture the window's on-screen pixels, downscaled to <paramref name="maxWidth"/>. Runs
    /// on any thread (pure GDI). Returns null if the window has no area.</summary>
    public static Shot? Capture(IntPtr hwnd, int maxWidth)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        int sw = r.R - r.L, sh = r.B - r.T;
        if (sw <= 0 || sh <= 0) return null;
        int tw = Math.Min(maxWidth, sw);
        int th = Math.Max(1, (int)((long)sh * tw / sw));

        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, tw, th);
        IntPtr old = SelectObject(mem, bmp);
        SetStretchBltMode(mem, HALFTONE);
        StretchBlt(mem, 0, 0, tw, th, screen, r.L, r.T, sw, sh, SRCCOPY);

        var bih = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = tw, biHeight = -th, biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var buf = new byte[tw * th * 4];
        int rows = GetDIBits(mem, bmp, 0, (uint)th, buf, ref bih, 0);

        SelectObject(mem, old);
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        if (rows == 0) return null;

        for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;   // GDI leaves alpha 0; force opaque
        return new Shot(buf, tw, th);
    }

    /// <summary>Turn a captured shot into a WriteableBitmap. Must be called on the UI thread.</summary>
    public static WriteableBitmap ToBitmap(Shot s)
    {
        var wb = new WriteableBitmap(s.Width, s.Height);
        using var stream = wb.PixelBuffer.AsStream();
        stream.Write(s.Bgra, 0, s.Bgra.Length);
        return wb;
    }
}
