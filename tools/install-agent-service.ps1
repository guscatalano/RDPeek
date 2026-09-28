<#
.SYNOPSIS
    Install the RDPeek Agent Service - an OPT-IN Windows Service that supervises the
    in-session agent (rdpeek-agent.exe serve) for resiliency: boot-time start, auto-restart
    on crash, and coverage of every active user session, centrally.

.DESCRIPTION
    REQUIRES ADMIN (an elevated shell). The service runs as LocalSystem and, because a
    service lives in session 0 (no interactive RDP DVC), it LAUNCHES the agent INTO each
    active user session rather than opening the channel itself.

    This is an ALTERNATIVE to the scheduled-task path (tools/install-agent-web.ps1) for
    locked-down / always-on hosts. The scheduled-task path is untouched and still works;
    do not run both at once (you'd get two agents per session).

    Creates a service named 'RdpeekAgentSvc' (start=auto, LocalSystem), pointing binPath at
    rdpeek-agent-service.exe, then starts it. The service auto-resolves rdpeek-agent.exe from
    its own folder (see AgentPathResolver); pass -AgentExePath to override.

.EXAMPLE
    .\install-agent-service.ps1 -ServiceExePath C:\Tools\RDPeek\rdpeek-agent-service.exe
.EXAMPLE
    # explicit agent path (e.g. agent installed elsewhere):
    .\install-agent-service.ps1 -ServiceExePath C:\Tools\RDPeek\rdpeek-agent-service.exe -AgentExePath C:\Agent\rdpeek-agent.exe
#>
[CmdletBinding()]
param(
    # Defaults to the exe next to this script - so the released server bundle (exe + script together)
    # installs with no arguments. Pass a path when the script and exe live apart.
    [string] $ServiceExePath = (Join-Path $PSScriptRoot 'rdpeek-agent-service.exe'),
    [string] $AgentExePath,
    [string] $ServiceName = 'RdpeekAgentSvc'
)

$ErrorActionPreference = 'Stop'

# --- must be elevated: LocalSystem service creation + per-session task registration need admin ---
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    throw "This installer requires an ELEVATED (Run as administrator) PowerShell. The service runs as LocalSystem."
}

if (-not (Test-Path $ServiceExePath)) { throw "Service exe not found: $ServiceExePath" }
$ServiceExePath = (Resolve-Path $ServiceExePath).Path

# Build the binPath. Quote the exe (may contain spaces); append --agent only if overridden.
# --service forces service mode even if something makes UserInteractive look true.
$binPath = "`"$ServiceExePath`" --service"
if ($AgentExePath) {
    if (-not (Test-Path $AgentExePath)) { throw "Agent exe not found: $AgentExePath" }
    $AgentExePath = (Resolve-Path $AgentExePath).Path
    $binPath += " --agent `"$AgentExePath`""
}

# If the service already exists, stop + remove it first so we can re-point binPath.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists - stopping and recreating." -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue }
    # sc.exe delete is the reliable cross-version removal.
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Milliseconds 500
}

# Create as auto-start LocalSystem. New-Service defaults obj= LocalSystem.
New-Service -Name $ServiceName `
            -BinaryPathName $binPath `
            -DisplayName 'RDPeek Agent Service' `
            -Description 'Supervises the in-session RDPeek agent: launches rdpeek-agent.exe into each active user session and relaunches it on crash. Runs as LocalSystem (session 0).' `
            -StartupType Automatic | Out-Null

# Ask the SCM to also restart the service itself on failure (defence in depth on top of the
# service's own per-agent supervision). 5s, 10s, then every 30s; reset the counter daily.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Start-Service -Name $ServiceName

Write-Host "Installed and started service '$ServiceName' (LocalSystem, start=auto)." -ForegroundColor Green
Write-Host "  Service exe : $ServiceExePath"
if ($AgentExePath) { Write-Host "  Agent exe   : $AgentExePath (explicit)" }
else               { Write-Host "  Agent exe   : auto-resolved next to the service exe (rdpeek-agent.exe)" }
Write-Host ""
Write-Host "The service launches the agent into every active user session and on RDP connect/logon." -ForegroundColor Cyan
Write-Host "NOTE: this is an alternative to the scheduled-task path (install-agent-web.ps1). Don't run both." -ForegroundColor Yellow
Write-Host "Uninstall with:  .\uninstall-agent-service.ps1"
