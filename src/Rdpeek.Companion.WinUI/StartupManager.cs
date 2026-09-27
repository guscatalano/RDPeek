using System;
using Microsoft.Win32;

namespace Rdpeek.Companion.WinUI;

/// <summary>Run-on-logon for the Companion via the per-user HKCU Run key (no admin). When enabled it
/// launches with <c>--tray</c> so it comes up hidden to the tray, not with the dashboard in your face.</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RDPeek Companion";
    public const string TrayArg = "--tray";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (k is null) return;
            if (on)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) k.SetValue(ValueName, $"\"{exe}\" {TrayArg}");
            }
            else
            {
                k.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best-effort — a failed toggle just leaves the previous state */ }
    }
}
