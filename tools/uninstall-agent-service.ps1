<#
.SYNOPSIS
    Uninstall the RDPeek Agent Service (RdpeekAgentSvc).

.DESCRIPTION
    REQUIRES ADMIN (elevated shell). Stops and deletes the service. On stop, the service
    terminates the agents it launched into user sessions; any straggler rdpeek-agent
    processes are then cleaned up. Does NOT touch the scheduled-task path
    (install-agent-web.ps1 / 'RDPeek Agent' task).

.EXAMPLE
    .\uninstall-agent-service.ps1
#>
[CmdletBinding()]
param(
    [string] $ServiceName = 'RdpeekAgentSvc'
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    throw "This uninstaller requires an ELEVATED (Run as administrator) PowerShell."
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$ServiceName' not present."
} else {
    if ($svc.Status -ne 'Stopped') {
        Write-Host "Stopping '$ServiceName' (it will terminate the agents it started)..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    }
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Removed service '$ServiceName'." -ForegroundColor Yellow
}

# Best-effort cleanup of any agent the service may have left behind.
Get-Process rdpeek-agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# The service launches the agent via per-session tasks under the \RDPeek\ folder
# (Agent-S<sessionId>). Remove any strays. This is the service's OWN folder and does not
# touch the 'RDPeek Agent' task created by install-agent-web.ps1 (which lives at the root).
Get-ScheduledTask -TaskPath '\RDPeek\' -ErrorAction SilentlyContinue |
    Where-Object { $_.TaskName -like 'Agent-S*' } |
    ForEach-Object {
        Write-Host "Removing per-session task \RDPeek\$($_.TaskName)..."
        Unregister-ScheduledTask -TaskName $_.TaskName -TaskPath '\RDPeek\' -Confirm:$false -ErrorAction SilentlyContinue
    }

Write-Host "Uninstalled. (The scheduled-task path, if installed, is untouched.)" -ForegroundColor Green
