$ErrorActionPreference = 'Stop'
$repo = 'C:\Users\crimson\source\repos\DVC_Tools'
$root = $env:TEMP
$N = 3                                   # number of simulated RDP connections
# Friendly, dotless host names (trailing digit seeds a distinct OS/CPU persona in the fake agent).
$names = @('DEMO-SQL01','DEMO-WEB02','DEMO-APP03')

function Build([string]$proj,[string]$exe,[string]$extra=''){
  $a = @('build',(Join-Path $repo $proj),'-c','Debug','--nologo') ; if($extra){$a += $extra.Split(' ')}
  & dotnet @a | Out-Null
  (Get-ChildItem (Join-Path $repo "$proj\bin") -Recurse -Filter $exe | Sort-Object LastWriteTime -Desc | Select -First 1).FullName
}

Write-Host "Building agent, plugin, companion, mock..." -ForegroundColor Cyan
$agent  = Build 'src\Rdpeek.Agent'  'rdpeek-agent.exe'
$plugin = Build 'src\Rdpeek.Plugin' 'rdpeek-plugin.exe'
$comp   = Build 'src\Rdpeek.Companion.WinUI' 'Rdpeek.Companion.WinUI.exe' '-r win-x64'
$mock   = & (Join-Path $repo 'tools\get-mock-rdp.ps1') -Source auto | Select -Last 1

# A sample file to pull, shared by every agent (all use %TEMP% as their root).
$sample = Join-Path $root 'rdpeek-demo-pullme.bin'
if(-not(Test-Path $sample)){ $fs=[IO.File]::Create($sample); $b=New-Object byte[] (1MB); 1..5|%{$fs.Write($b,0,$b.Length)}; $fs.Close() }

# Register the (single) client plugin once; every mstsc loads it.
& (Join-Path $repo 'tools\register.ps1') -ExePath $plugin | Out-Null
Remove-Item (Join-Path $env:TEMP 'rdpeek-plugin.log') -ErrorAction SilentlyContinue

# Auto-dismiss every mstsc "connect / cert" prompt for the whole run.
$clk = Start-Job {
  Add-Type @"
using System;using System.Text;using System.Runtime.InteropServices;
public static class K{const uint C=0x00F5;delegate bool E(IntPtr h,IntPtr l);
[DllImport("user32.dll")]static extern bool EnumWindows(E c,IntPtr l);
[DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr p,E c,IntPtr l);
[DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder s,int n);
[DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
[DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
static string Cl(IntPtr h){var s=new StringBuilder(64);GetClassName(h,s,64);return s.ToString();}
static string Tx(IntPtr h){var s=new StringBuilder(256);GetWindowText(h,s,256);return s.ToString();}
public static void A(){EnumWindows((h,_)=>{if(IsWindowVisible(h)&&Cl(h)=="#32770"){EnumChildWindows(h,(c,__)=>{if(Cl(c)=="Button"){var t=Tx(c).Replace("&","").Trim();if(t=="Connect"||t=="Yes"||t=="OK")SendMessage(c,C,IntPtr.Zero,IntPtr.Zero);}return true;},IntPtr.Zero);}return true;},IntPtr.Zero);}}
"@
  1..120 | ForEach-Object { [K]::A(); Start-Sleep -Milliseconds 500 }
}

for($i=0; $i -lt $N; $i++){
  $ip   = "127.0.0.$(2+$i)"                  # distinct loopback address (no hosts file needed)
  $name = $names[$i]                         # what the agent reports as its hostname
  $tcp  = 9990 + $i
  $port = 33390 + $i
  $cer  = Join-Path $env:TEMP "rdpeek-multi-$i.cer"
  $mlog = Join-Path $env:TEMP "rdpeek-multi-$i.log"
  Remove-Item $cer -ErrorAction SilentlyContinue

  Write-Host "Connection $($i+1): mstsc $ip`:$port  ->  mock  ->  agent tcp:$tcp (host $name)" -ForegroundColor Cyan

  Start-Process $agent -WindowStyle Minimized -ArgumentList @(
    'serve-tcp',$tcp,'--fake','--fake-host',$name,'--file-root',$root) | Out-Null

  Start-Process $mock -WindowStyle Minimized -ArgumentList @(
    '--port',$port,'--desktop',
    '--dvc','dvc::diag::inspector','--dvc-bridge',"dvc::diag::inspector=127.0.0.1:$tcp",
    '--cert-out',$cer,'--log-file',$mlog) | Out-Null

  for($t=0; $t -lt 60 -and -not (Test-Path $cer); $t++){ Start-Sleep -Milliseconds 200 }
  if(-not(Test-Path $cer)){ Write-Host "  (mock $i didn't export its cert; skipping pin)" -ForegroundColor Yellow; continue }

  # Pin this mock's cert so mstsc connects without a trust prompt.
  $hash = ([Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)).GetCertHash()
  foreach($srv in @($ip, "$ip`:$port")){
    $k = New-Item -Path "HKCU:\Software\Microsoft\Terminal Server Client\Servers\$srv" -Force
    New-ItemProperty -Path $k.PSPath -Name CertHash -Value $hash -PropertyType Binary -Force | Out-Null
  }

  # Name the .rdp after the host name (no dots — mstsc truncates the title at the first dot).
  # mstsc titles the window "<basename> - <addr> - RDC" and the companion correlates on that
  # first token, so the basename must equal the host the agent reports.
  $rdp = Join-Path $env:TEMP "$name.rdp"
  @"
full address:s:$ip`:$port
authentication level:i:2
enablecredsspsupport:i:0
prompt for credentials:i:0
screen mode id:i:1
desktopwidth:i:1024
desktopheight:i:720
"@ | Set-Content $rdp -Encoding ASCII
  Start-Process mstsc.exe -ArgumentList $rdp | Out-Null
  Start-Sleep -Seconds 3          # stagger so the auto-clicker keeps up
}

Start-Sleep -Seconds 4
$plog = if(Test-Path (Join-Path $env:TEMP 'rdpeek-plugin.log')){Get-Content (Join-Path $env:TEMP 'rdpeek-plugin.log') -Raw}else{''}
$loaded = ([regex]::Matches($plog,'build=rdpeek-agent')).Count
Write-Host "Plugin channels bridged to an agent: $loaded" -ForegroundColor Green

Write-Host "Launching the companion..." -ForegroundColor Cyan
Start-Process $comp | Out-Null

Write-Host "`nUP: $N agents + $N mocks + $N mstsc + companion." -ForegroundColor Green
Write-Host "The companion's Dashboard should list $N connections ($($names -join ', ')), each a different host persona."
Write-Host "Stop with:  Get-Process rdpeek-agent,MockRdp,mstsc,Rdpeek.Companion.WinUI | Stop-Process -Force; $repo\tools\unregister.ps1"
