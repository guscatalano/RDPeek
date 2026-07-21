<#
.SYNOPSIS
    Clipboard uninstaller: remove the RDPeek agent auto-start.
    Paste inside the remote session:

        irm https://raw.githubusercontent.com/guscatalano/RDPeek/main/tools/uninstall-agent-web.ps1 | iex
#>

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'RDPeek'

$svc = New-Object -ComObject 'Schedule.Service'
$svc.Connect()
$root = $svc.GetFolder('\')
try
{
    $root.DeleteTask('RDPeek Agent', 0)
    Write-Host "Removed scheduled task 'RDPeek Agent'." -ForegroundColor Yellow
}
catch
{
    Write-Host "Scheduled task 'RDPeek Agent' not present."
}

Get-Process rdpeek-agent -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $installDir)
{
    Remove-Item $installDir -Recurse -Force
    Write-Host "Removed $installDir." -ForegroundColor Yellow
}

Write-Host "Uninstalled." -ForegroundColor Green
