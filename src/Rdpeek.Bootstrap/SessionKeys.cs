using System.Runtime.InteropServices;

namespace Rdpeek.Bootstrap;

/// <summary>
/// Last-resort keyboard injection into the RDP session, used only when AlternateShell/StartProgram
/// provisioning produced no agent check-in. It taps <c>Win+R</c> and types a command into the
/// session's Run dialog.
///
/// <para><b>Window-targeted, not global.</b> Keys are <c>PostMessage</c>d directly to the
/// <c>mstscax</c> control's own input child window — so they reach the RDP session regardless of
/// foreground focus and can never leak onto the local desktop (unlike global <c>SendInput</c>).
/// Each message carries the real hardware scancode in its lParam, because RDP forwards scancodes.
/// For <c>Win+R</c> to reach the session, the control's <c>KeyboardHookMode</c> must be 1 (send
/// Windows-key combinations to the remote computer). This is still best-effort/fragile — hence
/// "last resort".</para>
/// </summary>
internal static class SessionKeys
{
    /// <summary>Find mstscax's keyboard sink under the RDP control. Its window tree is
    /// UIMainClass → UIContainerClass → { IHWindowClass (input host), OPContainerClass → OPWindowClass
    /// (output/render) }. Keystrokes go to <b>IHWindowClass</b> — NOT the deepest window (that's the
    /// render surface, which ignores keys). Prefer IHWindowClass; fall back to the deepest child.</summary>
    public static IntPtr FindInputWindow(IntPtr root)
    {
        var all = Descendants(root).ToList();
        foreach (var (hwnd, cls, _) in all)
            if (cls.Contains("IHWindow", StringComparison.OrdinalIgnoreCase))
                return hwnd;

        var best = root;
        foreach (var (hwnd, _, depth) in all)
            if (depth > Depth(root, best)) best = hwnd;
        return best;
    }

    /// <summary>Enumerate (hwnd, class, depth) for every descendant of <paramref name="root"/> — used
    /// to discover which child is the input sink.</summary>
    public static IEnumerable<(IntPtr hwnd, string cls, int depth)> Descendants(IntPtr root)
    {
        var results = new List<(IntPtr, string, int)>();
        void Walk(IntPtr parent, int depth)
        {
            IntPtr child = GetWindow(parent, GW_CHILD);
            while (child != IntPtr.Zero)
            {
                results.Add((child, ClassOf(child), depth));
                Walk(child, depth + 1);
                child = GetWindow(child, GW_HWNDNEXT);
            }
        }
        Walk(root, 1);
        return results;
    }

    /// <summary>Open the session Run dialog on <paramref name="target"/>, type
    /// <paramref name="command"/>, press Enter.</summary>
    public static void RunViaWinR(IntPtr target, string command)
    {
        TapWinR(target);
        Thread.Sleep(900);          // let the Run dialog open in the session
        TypeText(target, command);
        Thread.Sleep(250);
        Tap(target, VK_RETURN);
    }

    // ── key helpers ───────────────────────────────────────────────────────────

    private static void TapWinR(IntPtr h)
    {
        // Hold Left-Win (extended), tap R, release Win.
        KeyDown(h, VK_LWIN, extended: true);
        Thread.Sleep(40);
        KeyDown(h, VK_R);
        Thread.Sleep(40);
        KeyUp(h, VK_R);
        Thread.Sleep(40);
        KeyUp(h, VK_LWIN, extended: true);
    }

    private static void TypeText(IntPtr h, string text)
    {
        foreach (char ch in text)
        {
            short vk = VkKeyScan(ch);
            if (vk == -1) continue;                 // not typeable on this layout
            bool shift = (vk & 0x0100) != 0;        // high byte bit0 = Shift required
            byte code = (byte)(vk & 0xFF);

            if (shift) KeyDown(h, VK_SHIFT);
            KeyDown(h, code);
            KeyUp(h, code);
            if (shift) KeyUp(h, VK_SHIFT);
            Thread.Sleep(8);                        // brief spacing so the session keeps up
        }
    }

    private static void Tap(IntPtr h, byte vk) { KeyDown(h, vk); Thread.Sleep(30); KeyUp(h, vk); }

    private static void KeyDown(IntPtr h, byte vk, bool extended = false) => Post(h, vk, up: false, extended);
    private static void KeyUp(IntPtr h, byte vk, bool extended = false) => Post(h, vk, up: true, extended);

    private static void Post(IntPtr hwnd, byte vk, bool up, bool extended)
    {
        uint scan = MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        // lParam: repeat=1, scancode<<16, extended<<24; key-up adds prev-state + transition bits.
        uint lparam = 1u | (scan << 16);
        if (extended) lparam |= 1u << 24;
        if (up) lparam |= (1u << 30) | (1u << 31);
        uint msg = up ? WM_KEYUP : WM_KEYDOWN;
        PostMessage(hwnd, msg, (IntPtr)vk, (IntPtr)lparam);
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private static int Depth(IntPtr root, IntPtr hwnd)
    {
        int d = 0;
        for (var h = hwnd; h != IntPtr.Zero && h != root; h = GetParent(h)) d++;
        return d;
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private const byte VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_LWIN = 0x5B, VK_R = 0x52;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const uint GW_CHILD = 5, GW_HWNDNEXT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
}
