<#
.SYNOPSIS
    One-time setup on a machine you own: install the RDPeek agent and auto-start it on
    every RDP connection. Run this ONCE inside the remote session.

.DESCRIPTION
    - Copies rdpeek-agent.exe to a permanent local folder.
    - Registers a scheduled task triggered "on connection to user session" (RDP connect)
      that runs the agent in your interactive session — so future connects just work.
    - Starts it now so the current session works too.

    No admin required (per-user task, per-user folder). Reverse with unregister-agent-task.ps1.

    The agent is located next to this script (\publish\agent\rdpeek-agent.exe) — so the
    simplest invocation, from inside the session with drive redirection on, is:

        powershell -ExecutionPolicy Bypass -File \\tsclient\c\<path-to-repo>\tools\install-agent-task.ps1
#>
[CmdletBinding()]
param(
    [string] $AgentPath,
    [string] $InstallDir = "$env:LOCALAPPDATA\RDPeek",
    [string] $TaskName   = 'RDPeek Agent',
    [switch] $NoStart
)

$ErrorActionPreference = 'Stop'

# 1. Locate the agent (explicit -AgentPath, else relative to this script).
if (-not $AgentPath)
{
    $candidate = Join-Path $PSScriptRoot '..\publish\agent\rdpeek-agent.exe'
    if (Test-Path $candidate) { $AgentPath = (Resolve-Path $candidate).Path }
}
if (-not $AgentPath -or -not (Test-Path $AgentPath))
{
    throw "rdpeek-agent.exe not found. Publish it first on the client:`n" +
          "  dotnet publish src/Rdpeek.Agent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/agent`n" +
          "or pass -AgentPath."
}

# 2. Copy to a permanent per-user location.
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$dest = Join-Path $InstallDir 'rdpeek-agent.exe'
Copy-Item $AgentPath $dest -Force
Write-Host "Installed agent -> $dest" -ForegroundColor Green

# 3. Register a scheduled task: on RDP connect, run the agent in the interactive session.
$user = "$env:USERDOMAIN\$env:USERNAME"

$svc = New-Object -ComObject 'Schedule.Service'
$svc.Connect()
$root = $svc.GetFolder('\')

$task = $svc.NewTask(0)
$task.RegistrationInfo.Description = 'RDPeek agent — auto-start on RDP connect'
$task.Principal.UserId    = $user
$task.Principal.LogonType = 3      # TASK_LOGON_INTERACTIVE_TOKEN (runs in the session, no password)
$task.Principal.RunLevel  = 0      # least privilege

# TASK_TRIGGER_SESSION_STATE_CHANGE = 11 ; TASK_REMOTE_CONNECT = 3
$trigger = $task.Triggers.Create(11)
$trigger.StateChange = 3
$trigger.UserId = $user

# TASK_ACTION_EXEC = 0
$action = $task.Actions.Create(0)
$action.Path = $dest
$action.Arguments = 'serve'

$s = $task.Settings
$s.MultipleInstances          = 2       # IgnoreNew — don't stack agents on reconnect
$s.DisallowStartIfOnBatteries = $false
$s.StopIfGoingOnBatteries     = $false
$s.StartWhenAvailable         = $true
$s.ExecutionTimeLimit         = 'PT0S'  # no time limit (serve runs for the session)

# TASK_CREATE_OR_UPDATE = 6 ; logon type 3 = interactive
$root.RegisterTaskDefinition($TaskName, $task, 6, $user, $null, 3) | Out-Null
Write-Host "Registered scheduled task '$TaskName' (trigger: on RDP connect)." -ForegroundColor Green

# 4. Start now so the current session works without reconnecting.
if (-not $NoStart)
{
    ($root.GetTask($TaskName)).Run($null) | Out-Null
    Write-Host "Started the agent for the current session." -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Every future RDP connection to this machine will auto-start the RDPeek agent." -ForegroundColor Cyan
