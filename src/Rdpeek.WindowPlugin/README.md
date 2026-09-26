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

## Driving it from the Companion

The Companion (`Rdpeek.Companion.WinUI`) has a **Window** tab (under *Actions*) that is the client end
of this pipe: buttons for title tag/restore, minimize/maximize/restore, topmost on/off, taskbar flash,
and a HUD overlay text box. It connects to `\\.\pipe\rdpeek-window` per command (connect-write-close),
off the UI thread, so a missing plugin never hangs the dashboard.

## Lifecycle (verified live against the mock)

mstsc loads the DLL in-process and calls `IWTSPlugin::Initialize` — then **releases the plugin object
right away**, because we never create a DVC channel listener. So `Connected`/`Disconnected`/`Terminated`
generally never fire. That's fine by design: the class factory starts the UI + pipe + attach threads,
which **outlive the COM object** (`DllCanUnloadNow` returns `S_FALSE`, so the DLL stays mapped), and an
`AttachThread` binds to the session window and captures its real title independently of `Connected`.
The pipe drives everything from there. Confirmed end-to-end: title tag+restore, show state, topmost,
flash and the overlay all act on the live `mstsc` window.

## Notes / gotchas

- **CLSID** `{7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE03}`, distinct from the diag plugin (`…EE01`) and
  the mock's echo client (`…EE02`). **IWTSPlugin** IID `A1230201-1439-4e62-a414-190d0ac3d40e`.
- The control pipe is a single fixed instance (`nMaxInstances = 1`). With several mstsc windows (each
  its own process), only the first plugin instance wins the pipe; per-PID pipe names would be the fix
  for driving a specific window in a multi-connection setup. Single-window works today.
- `DllCanUnloadNow` returns `S_FALSE` on purpose (see Lifecycle): background threads outlive the COM
  objects, so the DLL must stay mapped rather than be unloaded from under them.
- Because the object is released after `Initialize`, mstsc keeps the DLL locked for the life of the
  process — to rebuild, close that `mstsc` first.
- x64 only, to match `mstsc`.
