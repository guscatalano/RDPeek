using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rdpeek.Companion;

/// <summary>
/// Bootstraps the agent inside a specific RDP session by driving that session's mstsc
/// window from outside: put the command on the clipboard, focus the window, then
/// SendInput Win+R → Ctrl+V → Enter. The command copies the agent from the redirected
/// drive (\\tsclient) into the remote %TEMP% and runs it.
///
/// Operator-initiated automation of the operator's own authorized session — it drives
/// a window the user chose, doing what they would type by hand.
/// </summary>
public static class InputBootstrap
{
    // --- command construction (pure, testable) ---

    /// <summary>C:\dir\file → \\tsclient\c\dir\file (the drive as seen inside the session).</summary>
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
        // Runs from the session's Run dialog. -NoExit keeps the serve window open.
        return "powershell -ExecutionPolicy Bypass -NoExit -Command " +
               $"\"$d=Join-Path $env:TEMP 'rdpeek-agent.exe'; Copy-Item '{src}' $d -Force; & $d serve\"";
    }

    // --- input injection ---

    public static async Task RunAsync(IntPtr targetWindow, string localAgentPath, int stepDelayMs = 400)
    {
        string command = BuildCommand(localAgentPath);

        string? savedClipboard = null;
        try { if (Clipboard.ContainsText()) savedClipboard = Clipboard.GetText(); } catch { /* ignore */ }
        Clipboard.SetText(command);

        try
        {
            SetForegroundWindow(targetWindow);
            await Task.Delay(stepDelayMs);       // let focus settle into the session
            SendCombo(VK_LWIN, VK_R);             // Win+R → Run dialog
            await Task.Delay(stepDelayMs * 2);    // Run dialog needs a moment to appear
            SendCombo(VK_CONTROL, VK_V);          // paste the command (via clipboard redirection)
            await Task.Delay(stepDelayMs);
            SendKey(VK_RETURN);                   // execute
            await Task.Delay(stepDelayMs);
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

    private const ushort VK_LWIN = 0x5B, VK_CONTROL = 0x11, VK_RETURN = 0x0D, VK_R = 0x52, VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint INPUT_KEYBOARD = 1;

    private static void SendKey(ushort vk)
        => Send(KeyDown(vk), KeyUp(vk));

    private static void SendCombo(ushort modifier, ushort key)
        => Send(KeyDown(modifier), KeyDown(key), KeyUp(key), KeyUp(modifier));

    private static INPUT KeyDown(ushort vk) => MakeKey(vk, 0);
    private static INPUT KeyUp(ushort vk) => MakeKey(vk, KEYEVENTF_KEYUP);

    private static INPUT MakeKey(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } },
    };

    private static void Send(params INPUT[] inputs)
        => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
