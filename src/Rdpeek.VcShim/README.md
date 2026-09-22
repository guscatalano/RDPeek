# rdpeek-vc-shim

A tiny native (C++) **DVC plugin DLL** that lets a *hosted* `mstscax` RDP control load
RDPeek's client plugin — which it otherwise can't.

## Why this exists

There are two ways a client DVC plugin gets loaded into an RDP session:

1. **COM AddIns** — `mstsc.exe` reads the Terminal Server Client `AddIns` registry and
   activates a `LocalServer32` COM object (`IWTSPlugin`) by CLSID. This is how RDPeek's
   plugin ships (out-of-process, so a plugin crash can't kill the host).
2. **PluginDlls** — a *hosted* `mstscax` control (e.g. `Rdpeek.Bootstrap`'s connect probe)
   **ignores those AddIns**. It only loads plugin DLLs listed in its `PluginDlls` advanced
   setting and calls each DLL's exported `VirtualChannelGetInstance` to get the `IWTSPlugin`
   (the DLL-export model, MS-RDPEDYC).

*Verified 2026-09-20:* even the `NotSafeForScripting` control does **not** load the COM
AddIns, so a registered RDPeek plugin never loads into a hosted control on its own — the
Bootstrap connected but rejected the diag channels (`0xC0000001`).

This shim bridges (2) → RDPeek's existing (1): its `VirtualChannelGetInstance` export simply
`CoCreateInstance`s the already-registered RDPeek COM plugin (CLSID
`{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE01}`) and hands the client that same `IWTSPlugin`. The
plugin itself is **unchanged** and still runs out-of-process.

Native C++ on purpose: a DVC plugin DLL is loaded straight into the host's process, so this
avoids dragging a managed runtime in-process just to forward one COM pointer.

## Build

Needs Visual Studio with the **Desktop development with C++** workload (for `cl.exe`).

```powershell
.\build.cmd
```

Auto-locates VS via `vswhere`, runs `VsDevCmd -arch=x64`, and compiles
`bin\rdpeek-vc-shim.dll` (x64, one clean export: `VirtualChannelGetInstance`). Verify with:

```powershell
dumpbin /exports bin\rdpeek-vc-shim.dll
```

Not part of `RDPeek.slnx` — it's a native project, built by `build.cmd`, not `dotnet`.

## Use

First register the RDPeek COM plugin (`tools/register.ps1`). Then point a hosted control at
the shim. Via the Bootstrap probe:

```powershell
rdpeek-bootstrap --connect 127.0.0.1:33389 --plugin-dll <path>\rdpeek-vc-shim.dll --hold 15
```

The plugin then loads headlessly (no `mstsc.exe`); check `%TEMP%\rdpeek-plugin.log` for
`OnNewChannelConnection — accepting`.
