<#
.SYNOPSIS
    Remove an RDPeek client DVC plugin registration created by register.ps1.

.EXAMPLE
    .\unregister.ps1
.EXAMPLE
    .\unregister.ps1 -Machine   # if registered with -Machine (elevated)
#>
[CmdletBinding()]
param(
    [string] $PluginName = 'RDPeek',
    [string] $Clsid      = '{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE01}',
    [switch] $Machine
)

$ErrorActionPreference = 'Stop'

if ($Machine) {
    $classesRoot = 'HKLM:\Software\Classes'
    $addinsRoot  = 'HKLM:\Software\Microsoft\Terminal Server Client\Default\AddIns'
} else {
    $classesRoot = 'HKCU:\Software\Classes'
    $addinsRoot  = 'HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns'
}

$clsidKey = "$classesRoot\CLSID\$Clsid"
$addinKey = "$addinsRoot\$PluginName"

foreach ($k in @($addinKey, $clsidKey)) {
    if (Test-Path $k) {
        Remove-Item -Path $k -Recurse -Force
        Write-Host "Removed $k" -ForegroundColor Yellow
    } else {
        Write-Host "Not present: $k"
    }
}

Write-Host "Unregistered '$PluginName'." -ForegroundColor Green
