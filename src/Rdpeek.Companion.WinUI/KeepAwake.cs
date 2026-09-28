using System;
using System.Runtime.InteropServices;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// Anti-idle helper for the "Keep session awake" toggle. Two independent mechanisms:
///  1. <see cref="Nudge"/> injects a single benign keystroke (VK_F15 down+up) via SendInput. F15 does
///     nothing in normal apps, but any input resets the RDP/session idle timer. It reaches whatever
///     window is focused; when an RDP session is focused/fullscreen the keystroke keeps that session alive.
///  2. <see cref="Hold"/> / <see cref="Release"/> use SetThreadExecutionState so the LOCAL machine won't
///     sleep or start the screensaver while the toggle is on.
/// Every P/Invoke is wrapped so a failure can never throw into the timer tick that drives this.
/// </summary>
internal static class KeepAwake
{
    // ── SendInput ──────────────────────────────────────────────────────────
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_F15 = 0x7E;   // a key no normal app reacts to

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>Inject one harmless F15 down+up keystroke. Never throws.</summary>
    public static void Nudge()
    {
        try
        {
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki = new KEYBDINPUT { wVk = VK_F15 };
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].u.ki = new KEYBDINPUT { wVk = VK_F15, dwFlags = KEYEVENTF_KEYUP };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
        catch { /* best-effort — never break the timer tick */ }
    }

    // ── SetThreadExecutionState ────────────────────────────────────────────
    [Flags]
    private enum ExecutionState : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002,
    }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    /// <summary>Request that the local machine stay awake with the display on until <see cref="Release"/>.</summary>
    public static void Hold()
    {
        try
        {
            SetThreadExecutionState(ExecutionState.ES_CONTINUOUS |
                                    ExecutionState.ES_SYSTEM_REQUIRED |
                                    ExecutionState.ES_DISPLAY_REQUIRED);
        }
        catch { }
    }

    /// <summary>Clear the keep-awake request; the machine may sleep/screensaver again on its own schedule.</summary>
    public static void Release()
    {
        try { SetThreadExecutionState(ExecutionState.ES_CONTINUOUS); }
        catch { }
    }
}
