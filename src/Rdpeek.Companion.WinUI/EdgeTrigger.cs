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
    private volatile bool _dockRight;
    private volatile bool _enabled = true;
    private bool _armed = true;

    /// <summary>Which edge to watch — false = left (default), true = right.</summary>
    public bool DockRight { get => _dockRight; set => _dockRight = value; }

    /// <summary>Pause the edge reveal (e.g. when the floating button is used instead).</summary>
    public bool Enabled { get => _enabled; set => _enabled = value; }

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
                    int left = 0, top = 0, right = 1920, bottom = 1080;
                    if (SystemParametersInfoW(SPI_GETWORKAREA, 0, ref wa, 0)) { left = wa.Left; top = wa.Top; right = wa.Right; bottom = wa.Bottom; }

                    int centerY = (top + bottom) / 2;
                    const int band = 40;   // ~match the visible nub's vertical extent (nub is 72px tall)
                    bool nearMid = Math.Abs(p.Y - centerY) <= band;
                    bool inZone = _dockRight ? (p.X >= right - 2 && nearMid) : (p.X <= left + 2 && nearMid);
                    bool pulledAway = _dockRight ? (p.X < right - 40) : (p.X > left + 40);

                    if (inZone && _armed && _enabled) { _armed = false; _onTrigger(); }
                    else if (pulledAway) { _armed = true; }                 // re-arm after pulling away
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
