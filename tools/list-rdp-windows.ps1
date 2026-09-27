<#
.SYNOPSIS
    Dump every visible top-level window (process, class, title) so we can see what class/process
    the RDP client actually uses on THIS machine. RDPeek's switcher only recognises windows whose
    process is "mstsc" and whose class is "TscShellContainerClass"; if your client differs, run this
    while an RDP session is connected and share the row for your session window.

.EXAMPLE
    .\list-rdp-windows.ps1              # all visible titled windows
    .\list-rdp-windows.ps1 -Rdp        # only likely RDP clients (mstsc/msrdc/Windows App)
#>
[CmdletBinding()]
param([switch] $Rdp)

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Win {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
'@

$rows = New-Object System.Collections.ArrayList
$cb = [Win+EnumProc] {
    param($h, $l)
    if (-not [Win]::IsWindowVisible($h)) { return $true }
    $len = [Win]::GetWindowTextLength($h)
    if ($len -eq 0) { return $true }
    $t = New-Object System.Text.StringBuilder ($len + 1); [void][Win]::GetWindowText($h, $t, $t.Capacity)
    $c = New-Object System.Text.StringBuilder 128;        [void][Win]::GetClassName($h, $c, $c.Capacity)
    $pid = 0; [void][Win]::GetWindowThreadProcessId($h, [ref]$pid)
    $pn = try { (Get-Process -Id $pid -ErrorAction Stop).ProcessName } catch { "?" }
    [void]$rows.Add([pscustomobject]@{ Process = $pn; Class = $c.ToString(); Title = $t.ToString() })
    return $true
}
[void][Win]::EnumWindows($cb, [IntPtr]::Zero)

if ($Rdp) {
    $rows = $rows | Where-Object { $_.Process -match 'mstsc|msrdc|rdp|Windows365|WindowsApp' -or $_.Title -match 'Remote Desktop|Windows App' }
}
$rows | Sort-Object Process, Class | Format-Table -AutoSize -Wrap
