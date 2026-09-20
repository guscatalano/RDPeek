<#
.SYNOPSIS
    End-to-end DVC checkpoint: drive the RDPeek client plugin against the mock RDP server
    over a real dynamic virtual channel, using real mstsc.exe.

.DESCRIPTION
    Obtains MockRdp.exe, starts it opening the RDPeek diagnostics channel
    (dvc::diag::inspector) server-side, registers the RDPeek client plugin, then connects
    real mstsc.exe to it. mstsc loads the plugin (a hosted mstscax control does NOT — only
    mstsc.exe loads DVC AddIns), the mock opens the channel, and the plugin's listener
    accepts it. The script asserts the plugin logged "OnNewChannelConnection" and the mock
    logged "opened by client".

    REQUIRES AN INTERACTIVE DESKTOP: mstsc opens a real RDP window, and this dismisses its
    security/certificate warnings by clicking them. It registers the plugin per-user (HKCU)
    and unregisters on exit.

.PARAMETER Port
    Loopback port for the mock. Default 33890.

.PARAMETER Channel
    DVC name the mock opens. Default dvc::diag::inspector (what the plugin listens for).
#>
[CmdletBinding()]
param(
    [int]    $Port = 33890,
    [string] $Channel = 'dvc::diag::inspector',
    [ValidateSet('auto', 'build', 'download')]
    [string] $MockSource = 'auto'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$pluginLog = Join-Path $env:TEMP 'rdpeek-plugin.log'
$mockLog = Join-Path $env:TEMP ('rdpeek-mock-{0}.log' -f (Get-Random))
$rdpFile = Join-Path $env:TEMP ('rdpeek-mock-{0}.rdp' -f (Get-Random))
$mockProc = $null
$mstscProc = $null
$registered = $false

# Auto-accepts mstsc's per-connection dialogs (resource-redirection security warning and
# the self-signed-certificate warning) by clicking Connect/Yes/OK. Transient UI only —
# nothing is persisted (analogous to FreeRDP's /cert:ignore).
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class RdcDialogs {
  const uint BM_CLICK=0x00F5;
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  static string Txt(IntPtr h){ var s=new StringBuilder(256); GetWindowText(h,s,256); return s.ToString(); }
  static string Cls(IntPtr h){ var s=new StringBuilder(64); GetClassName(h,s,64); return s.ToString(); }
  public static void Accept(){
    EnumWindows((h,_)=>{
      if(IsWindowVisible(h) && Cls(h)=="#32770"){
        EnumChildWindows(h,(c,__)=>{
          if(Cls(c)=="Button"){
            var t=Txt(c).Replace("&","").Trim();
            if(t=="Connect"||t=="Yes"||t=="OK") SendMessage(c,BM_CLICK,IntPtr.Zero,IntPtr.Zero);
          }
          return true;
        },IntPtr.Zero);
      }
      return true;
    },IntPtr.Zero);
  }
}
'@

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
    # 1. Mock server + plugin.
    $mock = & (Join-Path $PSScriptRoot 'get-mock-rdp.ps1') -Source $MockSource | Select-Object -Last 1
    $plugin = Build-Exe 'src\Rdpeek.Plugin' 'rdpeek-plugin.exe'

    # 2. Register the plugin (per-user) and clear the log we inspect.
    & (Join-Path $PSScriptRoot 'register.ps1') -ExePath $plugin
    $registered = $true
    try { [IO.File]::Delete($pluginLog) } catch {}

    # 3. Start the mock, opening the RDPeek channel server-side.
    Write-Host "Starting MockRdp on 127.0.0.1:$Port opening '$Channel' ..." -ForegroundColor Cyan
    $mockProc = Start-Process -FilePath $mock -PassThru -WindowStyle Hidden `
        -ArgumentList @('--port', $Port, '--dvc', $Channel, '--log-file', $mockLog, '--log-level', 'debug')

    $up = $false
    for ($i = 0; $i -lt 40; $i++)
    {
        try { (New-Object Net.Sockets.TcpClient).Connect('127.0.0.1', $Port); $up = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $up) { throw "MockRdp did not start listening on $Port." }

    # 4. Connect real mstsc.exe (TLS, no NLA), auto-accepting its warnings.
    @"
full address:s:127.0.0.1:$Port
authentication level:i:2
enablecredsspsupport:i:0
prompt for credentials:i:0
"@ | Set-Content -Path $rdpFile -Encoding ASCII

    Write-Host "Connecting mstsc.exe to the mock ..." -ForegroundColor Cyan
    $mstscProc = Start-Process -FilePath "$env:SystemRoot\System32\mstsc.exe" -PassThru -ArgumentList $rdpFile
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 400; [RdcDialogs]::Accept() }
    Start-Sleep -Seconds 2

    # 5. Verify both ends.
    $plog = (Test-Path $pluginLog) ? (Get-Content $pluginLog -Raw) : ''
    $mlog = (Test-Path $mockLog)   ? (Get-Content $mockLog -Raw)   : ''
    $pluginAccepted = $plog -match 'OnNewChannelConnection'
    $mockOpened     = $mlog -match 'opened by client'

    Write-Host ""
    Write-Host "plugin log : $pluginLog"
    Write-Host "mock log   : $mockLog"
    if ($pluginAccepted -and $mockOpened)
    {
        Write-Host "PASS — mstsc loaded the plugin; the mock opened '$Channel' and the plugin accepted it over DVC." -ForegroundColor Green
        exit 0
    }
    else
    {
        Write-Host ("FAIL — plugin accepted: {0}; mock opened: {1}." -f $pluginAccepted, $mockOpened) -ForegroundColor Red
        Write-Host "--- plugin log ---"; Write-Host $plog
        Write-Host "--- mock log ---";   Write-Host $mlog
        exit 1
    }
}
finally
{
    if ($mstscProc -and -not $mstscProc.HasExited) { $mstscProc | Stop-Process -Force -ErrorAction SilentlyContinue }
    if ($mockProc  -and -not $mockProc.HasExited)  { $mockProc  | Stop-Process -Force -ErrorAction SilentlyContinue }
    if ($registered) { & (Join-Path $PSScriptRoot 'unregister.ps1') 2>$null }
}
