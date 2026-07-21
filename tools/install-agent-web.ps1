<#
.SYNOPSIS
    Clipboard-only setup: download the RDPeek agent and auto-start it on every RDP connect.
    No drive redirection needed — only clipboard (to paste) + internet (to download).

.DESCRIPTION
    Run this INSIDE the remote session by pasting a single line:

        irm https://raw.githubusercontent.com/guscatalano/RDPeek/main/tools/install-agent-web.ps1 | iex

    It downloads rdpeek-agent.exe from the latest GitHub release, installs it per-user,
    registers a scheduled task triggered on RDP connect, and starts it now. Undo with
    unregister-agent-task.ps1 (or Task Scheduler → delete "RDPeek Agent").
#>

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'RDPeek'
$dest       = Join-Path $installDir 'rdpeek-agent.exe'
$url        = 'https://github.com/guscatalano/RDPeek/releases/latest/download/rdpeek-agent.exe'

New-Item -ItemType Directory -Force -Path $installDir | Out-Null

# Stop any running agent and wait for its exe to unlock (it locks itself while running).
Get-Process rdpeek-agent -ErrorAction SilentlyContinue | Stop-Process -Force
for ($i = 0; $i -lt 25; $i++)
{
    try { if (Test-Path $dest) { Remove-Item $dest -Force -ErrorAction Stop }; break }
    catch { Start-Sleep -Milliseconds 200 }
}

Write-Host "Downloading agent from $url ..."
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
Write-Host "Installed agent -> $dest" -ForegroundColor Green

# Register a scheduled task: on RDP connect, run the agent in the interactive session.
$user = "$env:USERDOMAIN\$env:USERNAME"

$svc = New-Object -ComObject 'Schedule.Service'
$svc.Connect()
$root = $svc.GetFolder('\')

$task = $svc.NewTask(0)
$task.RegistrationInfo.Description = 'RDPeek agent — auto-start on RDP connect'
$task.Principal.UserId    = $user
$task.Principal.LogonType = 3      # interactive token (runs in the session)
$task.Principal.RunLevel  = 0

$trigger = $task.Triggers.Create(11)   # TASK_TRIGGER_SESSION_STATE_CHANGE
$trigger.StateChange = 3               # TASK_REMOTE_CONNECT
$trigger.UserId = $user

$action = $task.Actions.Create(0)      # TASK_ACTION_EXEC
$action.Path = $dest
$action.Arguments = 'serve'

$s = $task.Settings
$s.MultipleInstances          = 2       # IgnoreNew
$s.DisallowStartIfOnBatteries = $false
$s.StopIfGoingOnBatteries     = $false
$s.StartWhenAvailable         = $true
$s.ExecutionTimeLimit         = 'PT0S'

$root.RegisterTaskDefinition('RDPeek Agent', $task, 6, $user, $null, 3) | Out-Null
Write-Host "Registered scheduled task 'RDPeek Agent' (on RDP connect)." -ForegroundColor Green

($root.GetTask('RDPeek Agent')).Run($null) | Out-Null
Write-Host "Started for the current session." -ForegroundColor Green
Write-Host ""
Write-Host "Done. Every future RDP connection will auto-start the RDPeek agent." -ForegroundColor Cyan
