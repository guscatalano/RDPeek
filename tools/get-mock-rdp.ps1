<#
.SYNOPSIS
    Obtain MockRdp.exe — the mock RDP server (github.com/guscatalano/MockRDPServer) — for
    RDPeek's DVC integration checkpoint.

.DESCRIPTION
    Prefers building from a sibling checkout (..\mock-rdp) when present, so you always test
    the local mock; otherwise downloads the self-contained exe published by the mock's CI to
    its latest GitHub Release. Returns the path to MockRdp.exe.

.PARAMETER OutDir
    Where to place MockRdp.exe. Default: <repo>\publish\mock.

.PARAMETER Source
    auto (default) | build | download. 'auto' builds from the sibling repo if found, else
    downloads.
#>
[CmdletBinding()]
param(
    [string] $OutDir = (Join-Path $PSScriptRoot '..\publish\mock'),
    [ValidateSet('auto', 'build', 'download')]
    [string] $Source = 'auto',
    [string] $RepoPath = (Join-Path $PSScriptRoot '..\..\mock-rdp'),
    [string] $ReleaseUrl = 'https://github.com/guscatalano/MockRDPServer/releases/latest/download/MockRdp.exe'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$dest = Join-Path (Resolve-Path $OutDir) 'MockRdp.exe'

$canBuild = Test-Path (Join-Path $RepoPath 'MockRdp.slnx')
$doBuild = $Source -eq 'build' -or ($Source -eq 'auto' -and $canBuild)

if ($doBuild)
{
    if (-not $canBuild) { throw "Source=build but no MockRdp.slnx under $RepoPath." }
    Write-Host "Building MockRdp from $RepoPath ..." -ForegroundColor Cyan
    $pub = Join-Path $RepoPath 'publish\mock'
    & dotnet publish (Join-Path $RepoPath 'src\MockRdp') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $pub
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
    Copy-Item (Join-Path $pub 'MockRdp.exe') $dest -Force
}
else
{
    Write-Host "Downloading MockRdp.exe from $ReleaseUrl ..." -ForegroundColor Cyan
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    try { (New-Object System.Net.WebClient).DownloadFile($ReleaseUrl, $dest) }
    catch
    {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $ReleaseUrl -OutFile $dest -UseBasicParsing
    }
}

Write-Host "MockRdp.exe -> $dest" -ForegroundColor Green
$dest
