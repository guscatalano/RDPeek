<#
.SYNOPSIS
    Headless end-to-end DVC checkpoint: load the RDPeek client plugin against the mock RDP server
    and verify the diagnostics handshake — no interactive desktop, so it runs in CI.

.DESCRIPTION
    A hosted mstscax control does NOT load the client COM AddIns that mstsc.exe does, so this uses
    rdpeek-vc-shim.dll (VirtualChannelGetInstance -> CoCreateInstance the registered RDPeek plugin)
    via the Bootstrap's --plugin-dll. Flow:
      1. obtain MockRdpCli.exe (build from ..\mock-rdp if present, else download the release)
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
    [int]    $HoldSeconds = 15,
    # Where a hang dump + managed stacks land if the bootstrap fails to self-terminate. CI uploads this.
    [string] $DumpDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'hang-artifacts')
)

$ErrorActionPreference = 'Stop'
$repo      = Resolve-Path (Join-Path $PSScriptRoot '..')
$pluginLog = Join-Path $env:TEMP 'rdpeek-plugin.log'
$mockLog   = Join-Path $env:TEMP ("rdpeek-mock-{0}.log" -f (Get-Random))
$certOut   = Join-Path $env:TEMP ("rdpeek-mock-{0}.cer" -f (Get-Random))
$bootOut   = Join-Path $env:TEMP 'rdpeek-bootstrap.out.log'
$bootErr   = Join-Path $env:TEMP 'rdpeek-bootstrap.err.log'
$mockProc  = $null
$registered = $false
$trustedThumb = $null
$hung      = $false

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
        '--port', $Port, '--dvc', 'dvc::diag::inspector,dvc::diag::files', '--desktop',
        '--cert-out', $certOut, '--log-file', $mockLog, '--log-level', 'debug')

    $up = $false
    for ($i = 0; $i -lt 60; $i++) {
        try { (New-Object Net.Sockets.TcpClient).Connect('127.0.0.1', $Port); $up = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $up) { throw "MockRdp did not start listening on $Port." }
    for ($i = 0; $i -lt 40 -and -not (Test-Path $certOut); $i++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $certOut)) { throw "mock did not export its cert to $certOut." }

    # 5. Trust that exact cert so the headless control connects without a prompt. NB: Import-Certificate
    #    into Cert:\CurrentUser\Root pops a Win32 trust-confirmation dialog ("Do you want to install this
    #    certificate?") that blocks forever on a headless runner — confirmed by a captured hang dump
    #    (ImportCertificateCommand.ProcessRecord stuck). The raw X509Store API adds it silently, and
    #    mirrors the removal in the finally block below.
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certOut)
    $rootStore = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
    $rootStore.Open('ReadWrite')
    $rootStore.Add($cert)
    $rootStore.Close()
    $trustedThumb = $cert.Thumbprint
    Write-Host "Trusted mock cert $trustedThumb (CurrentUser\Root, removed on exit)." -ForegroundColor DarkGray

    # 6. Load the plugin via the shim over a headless connection. The hosted mstscax control has been
    #    seen to not tear down headlessly, hanging CI to the 6h default. Bound the wait to
    #    HoldSeconds + margin; on timeout, capture managed stacks + a full dump for offline debugging
    #    (uploaded as a CI artifact from $DumpDir), then force-kill it.
    Write-Host "Connecting the bootstrap (plugin via shim) ..." -ForegroundColor Cyan
    $bp = Start-Process -FilePath $bootstrap -PassThru -NoNewWindow `
        -RedirectStandardOutput $bootOut -RedirectStandardError $bootErr `
        -ArgumentList @('--connect', "127.0.0.1:$Port", '--plugin-dll', $shim, '--hold', $HoldSeconds)

    $deadlineSec = $HoldSeconds + 45
    if (-not $bp.WaitForExit($deadlineSec * 1000)) {
        $hung = $true
        Write-Warning "bootstrap (pid $($bp.Id)) did not exit within ${deadlineSec}s — capturing diagnostics to $DumpDir"
        New-Item -ItemType Directory -Force -Path $DumpDir | Out-Null
        # Managed stacks first (fast, human-readable triage), then a full dump (native + managed).
        try { & dotnet-stack report -p $bp.Id *> (Join-Path $DumpDir 'bootstrap-stacks.txt') }
        catch { "dotnet-stack failed: $_" | Out-File (Join-Path $DumpDir 'bootstrap-stacks.txt') }
        try { & dotnet-dump collect -p $bp.Id -o (Join-Path $DumpDir 'bootstrap.dmp') --type Full }
        catch { Write-Warning "dotnet-dump failed: $_" }
        # Snapshot the logs alongside the dump so the artifact is self-contained.
        foreach ($f in @($pluginLog, $mockLog, $bootOut, $bootErr)) {
            try { if (Test-Path $f) { Copy-Item $f $DumpDir -Force } } catch {}
        }
        try { $bp | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}
    }
    if (Test-Path $bootOut) { Write-Host (Get-Content $bootOut -Raw) }
    if (Test-Path $bootErr) { $e = Get-Content $bootErr -Raw; if ($e) { Write-Host "--- bootstrap stderr ---"; Write-Host $e } }

    # 7. Verify the plugin loaded and completed the diag handshake.
    Start-Sleep -Milliseconds 500
    $plog = (Test-Path $pluginLog) ? (Get-Content $pluginLog -Raw) : ''
    $accepted = $plog -match 'OnNewChannelConnection'
    $caps     = $plog -match 'agent capabilities'

    Write-Host ""
    Write-Host "plugin log : $pluginLog"
    if ($hung) {
        Write-Host ("FAIL - bootstrap hung; diagnostics in $DumpDir (handshake seen: accepted={0} caps={1})." -f $accepted, $caps) -ForegroundColor Red
        Write-Host "--- plugin log ---"; Write-Host $plog
        Write-Host "--- mock log ---";   if (Test-Path $mockLog) { Get-Content $mockLog -Raw | Write-Host }
        exit 1
    }
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
