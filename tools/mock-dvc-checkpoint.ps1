<#
.SYNOPSIS
    End-to-end DVC checkpoint: drive the RDPeek client plugin against the mock RDP server
    over a real dynamic virtual channel.

.DESCRIPTION
    Obtains MockRdp.exe, starts it opening the RDPeek diagnostics channel
    (dvc::diag::inspector) server-side, registers the RDPeek client plugin, then connects
    RDPeek's own mstscax host (rdpeek-bootstrap --connect) to it. The mock opens the DVC;
    mstscax loads the plugin, whose listener accepts the channel — logged to
    %TEMP%\rdpeek-plugin.log. The script asserts that "OnNewChannelConnection" appears.

    REQUIRES AN INTERACTIVE DESKTOP: mstscax hosts a real RDP window, so this cannot run on
    a headless CI agent. It registers the plugin per-user (HKCU) and unregisters on exit.

.PARAMETER Port
    Loopback port for the mock. Default 33890.

.PARAMETER HoldSeconds
    How long to hold the connection open after connecting. Default 8.
#>
[CmdletBinding()]
param(
    [int]    $Port = 33890,
    [string] $Channel = 'dvc::diag::inspector',
    [int]    $HoldSeconds = 8,
    [ValidateSet('auto', 'build', 'download')]
    [string] $MockSource = 'auto'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$pluginLog = Join-Path $env:TEMP 'rdpeek-plugin.log'
$mockProc = $null
$registered = $false

function Build-Exe([string] $project, [string] $exeName)
{
    & dotnet build (Join-Path $repo $project) -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed: $project" }
    $hit = Get-ChildItem (Join-Path $repo "$project\bin") -Recurse -Filter $exeName -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $hit) { throw "$exeName not found after building $project." }
    $hit.FullName
}

try
{
    # 1. Mock server + RDPeek binaries.
    $mock = & (Join-Path $PSScriptRoot 'get-mock-rdp.ps1') -Source $MockSource | Select-Object -Last 1
    $plugin = Build-Exe 'src\Rdpeek.Plugin' 'rdpeek-plugin.exe'
    $bootstrap = Build-Exe 'src\Rdpeek.Bootstrap' 'rdpeek-bootstrap.exe'

    # 2. Register the plugin (per-user) and clear the log we will inspect.
    & (Join-Path $PSScriptRoot 'register.ps1') -ExePath $plugin
    $registered = $true
    Remove-Item $pluginLog -ErrorAction SilentlyContinue

    # 3. Start the mock, opening the RDPeek channel server-side.
    Write-Host "Starting MockRdp on 127.0.0.1:$Port opening '$Channel' ..." -ForegroundColor Cyan
    $mockProc = Start-Process -FilePath $mock -PassThru -WindowStyle Hidden `
        -ArgumentList @('--port', $Port, '--dvc', $Channel, '--log-level', 'info')

    # Wait for the port to accept.
    $up = $false
    for ($i = 0; $i -lt 40; $i++)
    {
        try { (New-Object Net.Sockets.TcpClient).Connect('127.0.0.1', $Port); $up = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $up) { throw "MockRdp did not start listening on $Port." }

    # 4. Connect RDPeek's mstscax host; the plugin loads and its DVC connects.
    Write-Host "Connecting rdpeek-bootstrap to the mock (holding ${HoldSeconds}s) ..." -ForegroundColor Cyan
    & $bootstrap --connect "127.0.0.1:$Port" --hold $HoldSeconds
    $connectExit = $LASTEXITCODE

    # 5. Verify the plugin's listener accepted the channel.
    Start-Sleep -Milliseconds 500
    $log = (Test-Path $pluginLog) ? (Get-Content $pluginLog -Raw) : ''
    $sawChannel = $log -match 'OnNewChannelConnection'

    Write-Host ""
    Write-Host "connect exit code : $connectExit"
    Write-Host "plugin log        : $pluginLog"
    if ($sawChannel)
    {
        Write-Host "PASS — the mock opened '$Channel' and the plugin accepted it over DVC." -ForegroundColor Green
        exit 0
    }
    else
    {
        Write-Host "FAIL — no 'OnNewChannelConnection' in the plugin log. Log so far:" -ForegroundColor Red
        Write-Host $log
        exit 1
    }
}
finally
{
    if ($mockProc -and -not $mockProc.HasExited) { $mockProc | Stop-Process -Force -ErrorAction SilentlyContinue }
    if ($registered) { & (Join-Path $PSScriptRoot 'unregister.ps1') 2>$null }
}
