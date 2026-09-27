using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dvc.Diag.Protocol;
using Google.Protobuf;

namespace Rdpeek.Agent;

/// <summary>
/// Captures the agent's own session desktop as a JPEG. Because the agent runs inside the RDP session,
/// this is a live picture of what's on that session's screen — the viewer uses it for per-session
/// thumbnails that work even when the session is minimized/fullscreen on the client.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ScreenshotCollector
{
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    public static Screenshot Capture(int maxWidth, int quality)
    {
        if (maxWidth <= 0) maxWidth = 480;
        quality = quality is >= 1 and <= 100 ? quality : 60;
        try
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) return new Screenshot { Note = "no desktop metrics" };

            using var full = new Bitmap(vw, vh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(vx, vy, 0, 0, new Size(vw, vh), CopyPixelOperation.SourceCopy);

            int tw = Math.Min(maxWidth, vw);
            int th = Math.Max(1, (int)((long)vh * tw / vw));
            using var small = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, tw, th);
            }

            var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            using var ms = new MemoryStream();
            small.Save(ms, jpeg, ep);
            return new Screenshot { Jpeg = ByteString.CopyFrom(ms.ToArray()), Width = (uint)tw, Height = (uint)th };
        }
        catch (Exception ex)
        {
            return new Screenshot { Note = ex.Message };
        }
    }

    /// <summary>A synthetic placeholder image (used by the fake/demo agent so it doesn't leak the real
    /// local screen).</summary>
    public static Screenshot Placeholder(string label, int maxWidth, int quality)
    {
        if (maxWidth <= 0) maxWidth = 480;
        quality = quality is >= 1 and <= 100 ? quality : 60;
        int tw = maxWidth, th = maxWidth * 9 / 16;
        try
        {
            using var bmp = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(26, 30, 40));
                using var pen = new Pen(Color.FromArgb(60, 210, 190), 2);
                g.DrawRectangle(pen, 8, 8, tw - 17, th - 17);
                using var font = new Font("Segoe UI", Math.Max(9f, tw / 26f), FontStyle.Bold);
                using var brush = new SolidBrush(Color.FromArgb(60, 210, 190));
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(label, font, brush, new RectangleF(0, 0, tw, th), sf);
            }
            var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            using var ms = new MemoryStream();
            bmp.Save(ms, jpeg, ep);
            return new Screenshot { Jpeg = ByteString.CopyFrom(ms.ToArray()), Width = (uint)tw, Height = (uint)th };
        }
        catch (Exception ex) { return new Screenshot { Note = ex.Message }; }
    }
}
