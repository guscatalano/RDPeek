<#
.SYNOPSIS
    Remove the rdpeek-window-plugin in-process AddIn registration written by register.ps1.
.EXAMPLE
    .\unregister.ps1
.EXAMPLE
    .\unregister.ps1 -Machine   # elevated
#>
[CmdletBinding()]
param(
    [string] $PluginName = 'RDPeekWindow',
    [string] $Clsid      = '{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE03}',
    [switch] $Machine
)

$ErrorActionPreference = 'Stop'
$root = if ($Machine) { 'HKLM:' } else { 'HKCU:' }
$clsidKey = "$root\Software\Classes\CLSID\$Clsid"
$addinKey = "$root\Software\Microsoft\Terminal Server Client\Default\AddIns\$PluginName"

foreach ($k in @($addinKey, $clsidKey)) {
    if (Test-Path $k) { Remove-Item $k -Recurse -Force; Write-Host "Removed $k" }
    else { Write-Host "Not present: $k" -ForegroundColor DarkGray }
}
Write-Host "Unregistered '$PluginName'." -ForegroundColor Green
