<#
.SYNOPSIS
    Remove the RDPeek agent auto-start (undo install-agent-task.ps1).

.EXAMPLE
    .\unregister-agent-task.ps1
.EXAMPLE
    .\unregister-agent-task.ps1 -KeepFiles   # remove the task but leave the installed exe
#>
[CmdletBinding()]
param(
    [string] $TaskName   = 'RDPeek Agent',
    [string] $InstallDir = "$env:LOCALAPPDATA\RDPeek",
    [switch] $KeepFiles
)

$ErrorActionPreference = 'Stop'

$svc = New-Object -ComObject 'Schedule.Service'
$svc.Connect()
$root = $svc.GetFolder('\')
try
{
    $root.DeleteTask($TaskName, 0)
    Write-Host "Removed scheduled task '$TaskName'." -ForegroundColor Yellow
}
catch
{
    Write-Host "Scheduled task '$TaskName' not present."
}

Get-Process rdpeek-agent -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not $KeepFiles -and (Test-Path $InstallDir))
{
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir." -ForegroundColor Yellow
}

Write-Host "Uninstalled." -ForegroundColor Green
