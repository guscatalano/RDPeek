<#
.SYNOPSIS
    Register the RDPeek client DVC plugin (LocalServer32 COM activation) so mstsc loads it.

.DESCRIPTION
    Writes two things (per-user by default, or machine-wide with -Machine):
      1. ...\Software\Classes\CLSID\{Clsid}\LocalServer32  (default) = path to the plugin exe
      2. ...\Terminal Server Client\Default\AddIns\{PluginName}  Name = {Clsid}

    Per-user (HKCU) needs no admin and is what mstsc reads for the interactive user.
    NOTE: standalone tools (rdpeek-doctor's activation probe) cannot auto-launch a
    per-user LocalServer32 — only mstsc can. Use -Machine (requires an elevated shell)
    if you want to verify activation with rdpeek-doctor outside of an RDP session.

.EXAMPLE
    .\register.ps1 -ExePath C:\Tools\RDPeek\rdpeek-plugin.exe
.EXAMPLE
    .\register.ps1 -ExePath C:\Tools\RDPeek\rdpeek-plugin.exe -Machine   # elevated
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ExePath,
    [string] $PluginName = 'RDPeek',
    [string] $Clsid      = '{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE01}',  # RDPeek dev CLSID
    [switch] $Machine
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) { throw "Plugin exe not found: $ExePath" }
$ExePath = (Resolve-Path $ExePath).Path

if ($Machine) {
    $classesRoot = 'HKLM:\Software\Classes'
    $addinsRoot  = 'HKLM:\Software\Microsoft\Terminal Server Client\Default\AddIns'
    $scope = 'machine-wide (HKLM)'
} else {
    $classesRoot = 'HKCU:\Software\Classes'
    $addinsRoot  = 'HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns'
    $scope = 'per-user (HKCU)'
}

$clsidKey = "$classesRoot\CLSID\$Clsid\LocalServer32"
$addinKey = "$addinsRoot\$PluginName"

New-Item -Path $clsidKey -Force | Out-Null
Set-ItemProperty -Path $clsidKey -Name '(default)' -Value $ExePath

New-Item -Path $addinKey -Force | Out-Null
Set-ItemProperty -Path $addinKey -Name 'Name' -Value $Clsid

Write-Host "Registered '$PluginName' ($scope)" -ForegroundColor Green
Write-Host "  CLSID   : $Clsid"
Write-Host "  Server  : $ExePath"
Write-Host "Verify with:  rdpeek-doctor"
Write-Host "Then start (or reconnect) an RDP session for mstsc to load the plugin."
