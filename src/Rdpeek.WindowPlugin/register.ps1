<#
.SYNOPSIS
    Register rdpeek-window-plugin.dll as an IN-PROCESS DVC AddIn so mstsc loads it into its own
    process (where it can touch the session window).

.DESCRIPTION
    This is RDPeek's *second* AddIn: a tiny native COM in-proc server whose only job is window
    manipulation. It sits ALONGSIDE the out-of-process diag plugin (RDPeek / LocalServer32) — it
    does not replace it. Because it's in-process, a crash here can take mstsc down, so it's kept
    deliberately small and the heavy logic stays on the crash-isolated Companion side, which drives
    this over \.\pipe\rdpeek-window.

    Writes, per-user (HKCU, no admin) by default or machine-wide with -Machine:
      1. ...\Software\Classes\CLSID\{Clsid}\InProcServer32  (default) = path to the DLL
                                                            ThreadingModel = Both
      2. ...\Terminal Server Client\Default\AddIns\{PluginName}  Name = {Clsid}

.EXAMPLE
    .\register.ps1 -DllPath .\bin\rdpeek-window-plugin.dll
.EXAMPLE
    .\register.ps1 -DllPath C:\Tools\RDPeek\rdpeek-window-plugin.dll -Machine   # elevated
#>
[CmdletBinding()]
param(
    [string] $DllPath,
    [string] $PluginName = 'RDPeekWindow',
    [string] $Clsid      = '{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE03}',  # window-plugin dev CLSID
    [switch] $Machine
)

$ErrorActionPreference = 'Stop'
if (-not $DllPath) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Definition
    $DllPath = Join-Path $here 'bin\rdpeek-window-plugin.dll'
}
if (-not (Test-Path $DllPath)) { throw "Plugin DLL not found: $DllPath  (run build.cmd first)" }
$DllPath = (Resolve-Path $DllPath).Path

$root = if ($Machine) { 'HKLM:' } else { 'HKCU:' }
$scope = if ($Machine) { 'machine-wide (HKLM)' } else { 'per-user (HKCU)' }
$clsidKey = "$root\Software\Classes\CLSID\$Clsid\InProcServer32"
$addinKey = "$root\Software\Microsoft\Terminal Server Client\Default\AddIns\$PluginName"

New-Item -Path $clsidKey -Force | Out-Null
Set-ItemProperty -Path $clsidKey -Name '(default)'     -Value $DllPath
Set-ItemProperty -Path $clsidKey -Name 'ThreadingModel' -Value 'Both'

New-Item -Path $addinKey -Force | Out-Null
Set-ItemProperty -Path $addinKey -Name 'Name' -Value $Clsid

Write-Host "Registered '$PluginName' in-process AddIn ($scope)" -ForegroundColor Green
Write-Host "  CLSID  : $Clsid"
Write-Host "  Server : $DllPath  (InProcServer32, ThreadingModel=Both)"
Write-Host "This is a SECOND AddIn - the out-of-process RDPeek diag plugin stays registered too."
Write-Host "Reconnect an RDP session; mstsc loads this DLL in-process."
Write-Host 'Drive it:  echo title hello > \.\pipe\rdpeek-window   (or via the Companion)'
Write-Host "Watch:     $env:TEMP\rdpeek-window-plugin.log"
