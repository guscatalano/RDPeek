using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rdpeek.Companion;

/// <summary>
/// Bootstraps the agent inside a specific RDP session by driving that session's mstsc
/// window from outside: put the command on the clipboard, force the window to the
/// foreground, then SendInput Ctrl+V → Enter (and optionally Win+R first). The command
/// copies the agent from the redirected drive (\\tsclient) into the remote %TEMP% and
/// runs it.
///
/// mstsc only forwards keystrokes to the session while it truly has focus, so we use the
/// full AttachThreadInput foreground dance and verify it took before sending.
/// </summary>
public static class InputBootstrap
{
    // --- command construction (pure, testable) ---

    public static string LocalToTsclient(string localPath)
    {
        if (localPath.Length >= 2 && localPath[1] == ':')
        {
            char drive = char.ToLowerInvariant(localPath[0]);
            return $@"\\tsclient\{drive}{localPath[2..]}";
        }
        return localPath;
    }

    public static string BuildCommand(string localAgentPath)
    {
        string src = LocalToTsclient(localAgentPath);
        return "powershell -ExecutionPolicy Bypass -NoExit -Command " +
               $"\"$d=Join-Path $env:TEMP 'rdpeek-agent.exe'; Copy-Item '{src}' $d -Force; & $d serve\"";
    }

    // --- run ---

    /// <returns>true if the RDP window was successfully focused and input was sent.</returns>
    public static async Task<bool> RunAsync(IntPtr targetWindow, string localAgentPath, bool useWinR, int stepDelayMs = 400)
    {
        string command = BuildCommand(localAgentPath);

        string? savedClipboard = null;
        try { if (Clipboard.ContainsText()) savedClipboard = Clipboard.GetText(); } catch { /* ignore */ }
        Clipboard.SetText(command);

        try
        {
            bool focused = ForceForeground(targetWindow);
            await Task.Delay(stepDelayMs);            // let the session engage input
            if (!focused && GetForegroundWindow() != targetWindow)
            {
                ForceForeground(targetWindow);        // one more attempt
                await Task.Delay(stepDelayMs);
                focused = GetForegroundWindow() == targetWindow;
            }
            if (!focused) return false;               // don't fire keys at the wrong window

            if (useWinR)
            {
                SendCombo(VK_LWIN, VK_R);
                await Task.Delay(stepDelayMs * 2);
            }
            SendCombo(VK_CONTROL, VK_V);
            await Task.Delay(stepDelayMs);
            SendKey(VK_RETURN);
            await Task.Delay(stepDelayMs);
            return true;
        }
        finally
        {
            try
            {
                if (savedClipboard != null) Clipboard.SetText(savedClipboard);
                else Clipboard.Clear();
            }
            catch { /* ignore */ }
        }
    }

    // --- robust foreground ---

    private const int SW_RESTORE = 9;
    private const int ASFW_ANY = -1;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_SENDCHANGE = 0x0002;

    private static bool ForceForeground(IntPtr hWnd)
    {
        if (GetForegroundWindow() == hWnd) return true;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

        // Remove the foreground lock so SetForegroundWindow isn't ignored.
        SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);
        AllowSetForegroundWindow(ASFW_ANY);

        uint targetThread = GetWindowThreadProcessId(hWnd, out _);
        uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint thisThread = GetCurrentThreadId();

        // Attach input queues so the OS treats the SetForegroundWindow as "user-driven".
        bool a1 = foreThread != targetThread && AttachThreadInput(foreThread, targetThread, true);
        bool a2 = thisThread != targetThread && AttachThreadInput(thisThread, targetThread, true);
        try
        {
            BringWindowToTop(hWnd);
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
            SetFocus(hWnd);
        }
        finally
        {
            if (a2) AttachThreadInput(thisThread, targetThread, false);
            if (a1) AttachThreadInput(foreThread, targetThread, false);
        }

        return GetForegroundWindow() == hWnd;
    }

    // --- key input ---

    private const ushort VK_LWIN = 0x5B, VK_CONTROL = 0x11, VK_RETURN = 0x0D, VK_R = 0x52, VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint INPUT_KEYBOARD = 1;

    private static void SendKey(ushort vk) => Send(KeyDown(vk), KeyUp(vk));
    private static void SendCombo(ushort modifier, ushort key)
        => Send(KeyDown(modifier), KeyDown(key), KeyUp(key), KeyUp(modifier));

    private static INPUT KeyDown(ushort vk) => MakeKey(vk, 0);
    private static INPUT KeyUp(ushort vk) => MakeKey(vk, KEYEVENTF_KEYUP);
    private static INPUT MakeKey(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } },
    };
    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    // --- P/Invoke ---

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
}
