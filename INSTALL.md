# Installing & running RDPeek

RDPeek is Windows-only (x64). See [`README.md`](README.md) for what it is and
[`DESIGN.md`](DESIGN.md) for how it fits together.

> **Status:** the **Doctor** and the **agent collectors** work today. The client
> COM plugin and the live client↔agent channel round-trip are still in progress, so
> the "live session" steps below describe the intended flow and note what's pending.

## Prerequisites

- Windows 10/11 or Windows Server, 64-bit.
- To **build**: the [.NET SDK](https://dotnet.microsoft.com/download) (8, 9, or 10).
  Building the `.slnx` solution needs SDK 9.0.200+ (10.x recommended).
- To **run prebuilt binaries**: nothing — CI publishes self-contained single-file
  exes (no runtime install required).

## Get the binaries

### Option A — download from CI
Grab the `rdpeek-tools-win-x64` artifact from the latest successful
[CI run](https://github.com/guscatalano/RDPeek/actions). It contains
`rdpeek-doctor.exe` and `rdpeek-agent.exe`.

### Option B — build from source
```powershell
git clone https://github.com/guscatalano/RDPeek.git
cd RDPeek
dotnet build RDPeek.slnx -c Release
dotnet test  RDPeek.slnx           # optional: 19 tests
```
Or produce standalone exes:
```powershell
dotnet publish src/Rdpeek.Doctor -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/doctor
dotnet publish src/Rdpeek.Agent  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/agent
```

## Diagnose plugin registrations (Doctor)

Runs locally, no RDP session needed. Reports every registered DVC plugin, its
activation model, bitness, and whether it actually activates.

```powershell
rdpeek-doctor
```
Exit code `0` = no failures, `1` = at least one failure. CI-friendly.

## Run the agent

The agent runs **inside** the remote RDP session (copy `rdpeek-agent.exe` into the
session and run it there).

**Collectors self-test** — works anywhere, no channel needed:
```powershell
rdpeek-agent selftest
```
Prints a live host/session snapshot and the top processes.

**Serve over the DVC channel** — for a live session (needs the client plugin, below):
```powershell
rdpeek-agent serve
```
Opens `dvc::diag::inspector` and serves the collectors, re-opening across
disconnect/reconnect.

### Recommended: auto-start the agent (one command, once per machine)

On a machine **you own**, paste this single line into a PowerShell **inside the remote
session** (the RDPeek Companion has a button that copies it for you):

```powershell
irm https://raw.githubusercontent.com/guscatalano/RDPeek/main/tools/install-agent-web.ps1 | iex
```

It downloads the agent from the latest release, registers a scheduled task triggered
**on RDP connect** that runs `rdpeek-agent serve` in your session, and starts it now.
After this, **every future connection auto-starts the agent** — no keystrokes, no drive
redirection, normal desktop. (Needs internet + clipboard; no admin; per-user task/folder.)

Undo it any time:
```powershell
irm https://raw.githubusercontent.com/guscatalano/RDPeek/main/tools/uninstall-agent-web.ps1 | iex
```

## Register the client plugin

Register `rdpeek-plugin.exe` on the **client** machine (the one running `mstsc`) so
the RDP client loads it:

```powershell
tools\register.ps1 -ExePath C:\Tools\RDPeek\rdpeek-plugin.exe
rdpeek-doctor          # verify it resolves and activates
```

This writes a per-user (HKCU) `LocalServer32` CLSID and a Terminal Server Client
`AddIns\RDPeek` entry — no admin rights required. Start or reconnect an RDP session
for `mstsc` to pick up the plugin. After connecting, check the plugin log:

```powershell
Get-Content $env:TEMP\rdpeek-plugin.log
```

You should see the plugin initialize, the channel connect, and the remote host
snapshot it pulled from the agent.

> **HKCU vs machine-wide.** `mstsc` (interactive session) activates a per-user
> plugin fine. But `rdpeek-doctor`'s standalone activation probe **cannot**
> auto-launch a per-user `LocalServer32` — it reports a WARN explaining this, not a
> failure. To make Doctor's probe show a full PASS, register machine-wide from an
> **elevated** shell:
> ```powershell
> tools\register.ps1 -ExePath C:\Tools\RDPeek\rdpeek-plugin.exe -Machine
> ```

## Uninstall

```powershell
tools\unregister.ps1   # removes the client plugin registration
```
The agent leaves nothing installed on the host — just delete `rdpeek-agent.exe`.

## Security note

RDPeek enumerates remote processes and (later) transfers files. It ships
**read-only by default** and is intended for **authorized** admin / diagnostics /
development use. See the Safety section in [`README.md`](README.md).
