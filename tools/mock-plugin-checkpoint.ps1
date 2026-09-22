<#
.SYNOPSIS
    Headless end-to-end DVC checkpoint: load the RDPeek client plugin against the mock RDP server
    and verify the diagnostics handshake — no interactive desktop, so it runs in CI.

.DESCRIPTION
    A hosted mstscax control does NOT load the client COM AddIns that mstsc.exe does, so this uses
    rdpeek-vc-shim.dll (VirtualChannelGetInstance -> CoCreateInstance the registered RDPeek plugin)
    via the Bootstrap's --plugin-dll. Flow:
      1. obtain MockRdp.exe (build from ..\mock-rdp if present, else download the release)
      2. build the shim, build + register the plugin, build the bootstrap
      3. start the mock opening the diag channels, exporting its cert (--cert-out)
      4. trust that exact cert (CurrentUser\Root) so the headless control connects prompt-free
      5. rdpeek-bootstrap --connect --plugin-dll <shim>  loads the plugin over the DVC
      6. assert the plugin log shows OnNewChannelConnection + the mock's diag capabilities
    Everything is undone on exit (cert removed, plugin unregistered, mock stopped).

.PARAMETER Port
    Loopback port for the mock. Default 33895.
#>
[CmdletBinding()]
param(
    [int]    $Port = 33895,
    [ValidateSet('auto', 'build', 'download')]
    [string] $MockSource = 'auto',
    [int]    $HoldSeconds = 15
)

$ErrorActionPreference = 'Stop'
$repo      = Resolve-Path (Join-Path $PSScriptRoot '..')
$pluginLog = Join-Path $env:TEMP 'rdpeek-plugin.log'
$mockLog   = Join-Path $env:TEMP ("rdpeek-mock-{0}.log" -f (Get-Random))
$certOut   = Join-Path $env:TEMP ("rdpeek-mock-{0}.cer" -f (Get-Random))
$mockProc  = $null
$registered = $false
$trustedThumb = $null

function Build-Exe([string] $project, [string] $exeName) {
    & dotnet build (Join-Path $repo $project) -c Debug --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed: $project" }
    (Get-ChildItem (Join-Path $repo "$project\bin") -Recurse -Filter $exeName -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

try {
    # 1. Mock server.
    $mock = & (Join-Path $PSScriptRoot 'get-mock-rdp.ps1') -Source $MockSource | Select-Object -Last 1

    # 2. Shim (native), plugin, bootstrap.
    Write-Host "Building rdpeek-vc-shim ..." -ForegroundColor Cyan
    & (Join-Path $repo 'src\Rdpeek.VcShim\build.cmd') | Out-Null
    $shim = Join-Path $repo 'src\Rdpeek.VcShim\bin\rdpeek-vc-shim.dll'
    if (-not (Test-Path $shim)) { throw "shim not built: $shim" }
    $plugin    = Build-Exe 'src\Rdpeek.Plugin'    'rdpeek-plugin.exe'
    $bootstrap = Build-Exe 'src\Rdpeek.Bootstrap' 'rdpeek-bootstrap.exe'

    # 3. Register the plugin (per-user) and clear the log we inspect.
    & (Join-Path $PSScriptRoot 'register.ps1') -ExePath $plugin | Out-Null
    $registered = $true
    try { [IO.File]::Delete($pluginLog) } catch {}

    # 4. Start the mock, opening the RDPeek diag channels and exporting its cert.
    Write-Host "Starting MockRdp on 127.0.0.1:$Port ..." -ForegroundColor Cyan
    $mockProc = Start-Process -FilePath $mock -PassThru -WindowStyle Hidden -ArgumentList @(
        '--port', $Port, '--dvc', 'dvc::diag::inspector,dvc::diag::files',
        '--cert-out', $certOut, '--log-file', $mockLog, '--log-level', 'debug')

    $up = $false
    for ($i = 0; $i -lt 60; $i++) {
        try { (New-Object Net.Sockets.TcpClient).Connect('127.0.0.1', $Port); $up = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $up) { throw "MockRdp did not start listening on $Port." }
    for ($i = 0; $i -lt 40 -and -not (Test-Path $certOut); $i++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $certOut)) { throw "mock did not export its cert to $certOut." }

    # 5. Trust that exact cert so the headless control connects without a prompt.
    $imported = Import-Certificate -FilePath $certOut -CertStoreLocation Cert:\CurrentUser\Root
    $trustedThumb = $imported.Thumbprint
    Write-Host "Trusted mock cert $trustedThumb (CurrentUser\Root, removed on exit)." -ForegroundColor DarkGray

    # 6. Load the plugin via the shim over a headless connection.
    Write-Host "Connecting the bootstrap (plugin via shim) ..." -ForegroundColor Cyan
    & $bootstrap --connect "127.0.0.1:$Port" --plugin-dll $shim --hold $HoldSeconds | Write-Host

    # 7. Verify the plugin loaded and completed the diag handshake.
    Start-Sleep -Milliseconds 500
    $plog = (Test-Path $pluginLog) ? (Get-Content $pluginLog -Raw) : ''
    $accepted = $plog -match 'OnNewChannelConnection'
    $caps     = $plog -match 'agent capabilities'

    Write-Host ""
    Write-Host "plugin log : $pluginLog"
    if ($accepted -and $caps) {
        Write-Host "PASS - the shim loaded the RDPeek plugin headlessly; it accepted the DVC and got the mock's capabilities." -ForegroundColor Green
        exit 0
    }
    Write-Host ("FAIL - OnNewChannelConnection: {0}; capabilities: {1}." -f $accepted, $caps) -ForegroundColor Red
    Write-Host "--- plugin log ---"; Write-Host $plog
    Write-Host "--- mock log ---";   if (Test-Path $mockLog) { Get-Content $mockLog -Raw | Write-Host }
    exit 1
}
finally {
    # Cleanup must never change the exit code (a pass already exited 0 before this runs).
    try { if ($mockProc -and -not $mockProc.HasExited) { $mockProc | Stop-Process -Force -ErrorAction SilentlyContinue } } catch {}
    try { if ($registered) { & (Join-Path $PSScriptRoot 'unregister.ps1') 2>$null } } catch {}
    if ($trustedThumb) {
        # Removing from CurrentUser\Root wants a UI prompt on an interactive desktop; on a headless
        # CI runner it just succeeds. Either way, don't let it fail the run.
        try {
            $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
            $store.Open('ReadWrite')
            foreach ($c in @($store.Certificates | Where-Object Thumbprint -eq $trustedThumb)) { $store.Remove($c) }
            $store.Close()
        } catch {}
    }
    foreach ($f in @($certOut, $mockLog)) { try { Remove-Item $f -Force -ErrorAction SilentlyContinue } catch {} }
}
