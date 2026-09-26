using System.Runtime.InteropServices;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// Reveals the switcher by watching for the cursor slamming into the left edge of the primary work
/// area near its vertical centre — the classic "shove the mouse against the side" gesture. Polls the
/// cursor on a background thread (cheap, ~10 Hz) instead of installing a global hook, and fires once
/// per approach: it re-arms only after the cursor pulls back off the edge, so holding there doesn't
/// re-trigger. The callback runs on the poll thread; the handler marshals to the UI thread.
/// </summary>
public sealed class EdgeTrigger : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(uint action, uint uiParam, ref RECT pv, uint winIni);
    private const uint SPI_GETWORKAREA = 0x0030;

    private readonly Action _onTrigger;
    private readonly Thread _thread;
    private volatile bool _run = true;
    private bool _armed = true;

    public EdgeTrigger(Action onTrigger)
    {
        _onTrigger = onTrigger;
        _thread = new Thread(Loop) { IsBackground = true, Name = "rdpeek-edge" };
        _thread.Start();
    }

    private void Loop()
    {
        while (_run)
        {
            try
            {
                if (GetCursorPos(out var p))
                {
                    RECT wa = default;
                    int left = 0, top = 0, bottom = 1080;
                    if (SystemParametersInfoW(SPI_GETWORKAREA, 0, ref wa, 0)) { left = wa.Left; top = wa.Top; bottom = wa.Bottom; }

                    int centerY = (top + bottom) / 2;
                    int band = Math.Max(120, (bottom - top) / 6);           // a tall zone around the middle
                    bool inZone = p.X <= left + 2 && Math.Abs(p.Y - centerY) <= band;

                    if (inZone && _armed) { _armed = false; _onTrigger(); }
                    else if (p.X > left + 40) { _armed = true; }            // re-arm after pulling away
                }
            }
            catch { /* transient Win32 hiccup — keep polling */ }
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        _run = false;
        _thread.Join(500);
    }
}
