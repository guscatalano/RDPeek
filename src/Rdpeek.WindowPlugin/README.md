# Rdpeek.WindowPlugin — in-process mstsc window control

A tiny **native, in-process** DVC AddIn whose only job is to manipulate the `mstsc.exe` window
from *inside* mstsc's process. It's RDPeek's **second** AddIn — a "control plane" companion to the
out-of-process diagnostics plugin (`Rdpeek.Plugin`, `LocalServer32`), **not** a replacement for it.

## Why a second, in-process plugin?

The diag plugin runs **out-of-process** (`LocalServer32`) on purpose: a crash there can't take
`mstsc` down. But an out-of-process COM server can't cleanly touch mstsc's own window (move/resize/
dock/topmost/overlay) — those calls want to run inside mstsc. So window control lives here, in a
**deliberately tiny** native DLL (one `.cpp`, no CLR) that mstsc loads **in-process** via
`InProcServer32`. Its crash surface is a fraction of the full plugin, and the heavy logic stays on
the crash-isolated Companion side, which drives this over a simple text pipe.

Two AddIns, two trade-offs, side by side:

| | `Rdpeek.Plugin` (diag) | `Rdpeek.WindowPlugin` (this) |
|---|---|---|
| Activation | `LocalServer32` (out-of-proc) | `InProcServer32` (in mstsc) |
| Language | C# COM (built-in interop) | native C++ |
| Job | channels, collectors, broker | mstsc window manipulation |
| Crash blast radius | isolated from mstsc | shares mstsc's process |

## Control pipe

On `Connected()` it finds mstsc's top-level window (class `TscShellContainerClass`, this PID), tags
its title as proof of life, shows a HUD overlay, and serves a named pipe: `\.\pipe\rdpeek-window`.
Commands are newline- or message-delimited UTF-8:

| command | effect |
|---|---|
| `title <text>` | append `<text>` to the window title (empty restores) |
| `move <x> <y> <w> <h>` | reposition / resize |
| `topmost on\|off` | toggle always-on-top |
| `show min\|max\|restore` | window state |
| `overlay <text>` | show/update the HUD banner (empty text hides it) |
| `flash` | flash the taskbar button |

```powershell
'title hello from the pipe' | Out-File -Encoding ascii \.\pipe\rdpeek-window
'overlay recording…'        | Out-File -Encoding ascii \.\pipe\rdpeek-window
'topmost on'                | Out-File -Encoding ascii \.\pipe\rdpeek-window
```

## Build & register (Windows, x64, no admin)

```powershell
.\build.cmd                                   # -> bin\rdpeek-window-plugin.dll (needs VS C++ tools)
.\register.ps1                                 # per-user InProcServer32 + AddIn "RDPeekWindow"
# reconnect an RDP session; mstsc loads this DLL in-process
.\unregister.ps1
```

Diagnostics log: `%TEMP%\rdpeek-window-plugin.log`.

## Notes / gotchas

- **CLSID** `{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE03}`, distinct from the diag plugin (`…EE01`) and
  the mock's echo client (`…EE02`). **IWTSPlugin** IID `A1230201-1439-4e62-a414-190d0ac3d40e`.
- `DllCanUnloadNow` returns `S_FALSE` on purpose: background UI + pipe threads outlive the COM
  objects, so the DLL must stay mapped until the process exits rather than be unloaded from under
  them. That's normal for a plugin DLL with background threads.
- x64 only, to match `mstsc`.
