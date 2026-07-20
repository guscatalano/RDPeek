<#
.SYNOPSIS
    Register the RDPeek client DVC plugin (LocalServer32 COM activation) so mstsc loads it.

.DESCRIPTION
    Writes two things:
      1. HKCU\Software\Classes\CLSID\{Clsid}\LocalServer32  (default) = path to the plugin exe
      2. HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\{PluginName}  Name = {Clsid}

    Per-user (HKCU) by default so no admin rights are needed. Run rdpeek-doctor
    afterwards to verify the registration resolves and activates.

.EXAMPLE
    .\register.ps1 -ExePath C:\Tools\RDPeek\rdpeek-plugin.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ExePath,
    [string] $PluginName = 'RDPeek',
    [string] $Clsid      = '{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE01}'  # RDPeek dev CLSID
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) { throw "Plugin exe not found: $ExePath" }
$ExePath = (Resolve-Path $ExePath).Path

$clsidKey = "HKCU:\Software\Classes\CLSID\$Clsid\LocalServer32"
$addinKey = "HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\$PluginName"

New-Item -Path $clsidKey -Force | Out-Null
Set-ItemProperty -Path $clsidKey -Name '(default)' -Value $ExePath

New-Item -Path $addinKey -Force | Out-Null
Set-ItemProperty -Path $addinKey -Name 'Name' -Value $Clsid

Write-Host "Registered '$PluginName'" -ForegroundColor Green
Write-Host "  CLSID   : $Clsid"
Write-Host "  Server  : $ExePath"
Write-Host "Verify with:  rdpeek-doctor"
Write-Host "Then start (or reconnect) an RDP session for mstsc to load the plugin."
