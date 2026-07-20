<#
.SYNOPSIS
    Remove an RDPeek client DVC plugin registration created by register.ps1.

.EXAMPLE
    .\unregister.ps1
#>
[CmdletBinding()]
param(
    [string] $PluginName = 'RDPeek',
    [string] $Clsid      = '{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE01}'
)

$ErrorActionPreference = 'Stop'

$clsidKey = "HKCU:\Software\Classes\CLSID\$Clsid"
$addinKey = "HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\$PluginName"

foreach ($k in @($addinKey, $clsidKey)) {
    if (Test-Path $k) {
        Remove-Item -Path $k -Recurse -Force
        Write-Host "Removed $k" -ForegroundColor Yellow
    } else {
        Write-Host "Not present: $k"
    }
}

Write-Host "Unregistered '$PluginName'." -ForegroundColor Green
