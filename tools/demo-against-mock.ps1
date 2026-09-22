<#
.SYNOPSIS
    One-command RDPeek demo against the mock: starts the REAL agent, the mock bridging its
    diagnostics DVC to that agent, and registers the client plugin — then you connect mstsc and open
    the companion to see everything (host info, processes, file pull, frames) driven by the real
    agent through the mock. Ctrl+C tears it all down.

.DESCRIPTION
    No duplicated protocol: the mock relays the diag channel bytes to `rdpeek-agent serve-tcp`, so the
    genuine AgentCore serves the plugin. The agent runs on THIS machine, so the "remote host" you see
    in the companion is your own box (real data) — and file pull serves real files from --file-root.
#>
[CmdletBinding()]
param(
    [int]    $Port = 33389,          # RDP port the mock listens on (connect mstsc here)
    [int]    $TcpPort = 9999,        # agent serve-tcp port the mock bridges to
    [string] $FileRoot = $env:TEMP,  # what file pull may read
    [ValidateSet('auto', 'build', 'download')] [string] $MockSource = 'auto'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$agentProc = $null; $mockProc = $null; $registered = $false
$mockLog = Join-Path $env:TEMP 'rdpeek-demo-mock.log'

function Build-Exe([string] $project, [string] $exeName) {
    & dotnet build (Join-Path $repo $project) -c Debug --nologo | Out-Null
    (Get-ChildItem (Join-Path $repo "$project\bin") -Recurse -Filter $exeName |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

try {
    Write-Host "Building agent + plugin, fetching the mock..." -ForegroundColor Cyan
    $agent  = Build-Exe 'src\Rdpeek.Agent'  'rdpeek-agent.exe'
    $plugin = Build-Exe 'src\Rdpeek.Plugin' 'rdpeek-plugin.exe'
    $mock   = & (Join-Path $PSScriptRoot 'get-mock-rdp.ps1') -Source $MockSource | Select-Object -Last 1

    Write-Host "Starting the real agent (serve-tcp $TcpPort, file-root $FileRoot)..." -ForegroundColor Cyan
    $agentProc = Start-Process $agent -ArgumentList @('serve-tcp', $TcpPort, '--file-root', $FileRoot) -PassThru -WindowStyle Minimized

    Write-Host "Starting the mock on :$Port, bridging dvc::diag::inspector -> the agent..." -ForegroundColor Cyan
    $mockProc = Start-Process $mock -PassThru -WindowStyle Minimized -ArgumentList @(
        '--port', $Port, '--desktop',
        '--dvc', 'dvc::diag::inspector',
        '--dvc-bridge', "dvc::diag::inspector=127.0.0.1:$TcpPort",
        '--log-file', $mockLog)

    & (Join-Path $PSScriptRoot 'register.ps1') -ExePath $plugin | Out-Null
    $registered = $true

    Write-Host ""
    Write-Host "Ready. Now:" -ForegroundColor Green
    Write-Host "  1. Connect mstsc to 127.0.0.1:$Port  (accept the certificate warning)."
    Write-Host "     mstsc loads the plugin; it opens the diag DVC, which the mock bridges to the real agent."
    Write-Host "  2. Launch the companion:  dotnet run --project src\Rdpeek.Companion.WinUI -c Debug -r win-x64"
    Write-Host "     - Dashboard: your host's real CPU / RAM / uptime (the agent runs here)."
    Write-Host "     - Files:     pull a file under $FileRoot -> saved to your Downloads with a progress bar."
    Write-Host "     - Frames:    live frame counts as the plugin polls the agent."
    Write-Host ""
    Write-Host "Ctrl+C to stop everything." -ForegroundColor DarkGray
    while ($true) { Start-Sleep -Seconds 1 }
}
finally {
    Write-Host "`nStopping..." -ForegroundColor Cyan
    if ($registered) { & (Join-Path $PSScriptRoot 'unregister.ps1') 2>$null }
    foreach ($p in @($mockProc, $agentProc)) { if ($p -and -not $p.HasExited) { $p | Stop-Process -Force -ErrorAction SilentlyContinue } }
}
