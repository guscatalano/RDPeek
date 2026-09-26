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

A background thread finds mstsc's top-level window (class `TscShellContainerClass`, this PID) and serves
a per-process named pipe: `\\.\pipe\rdpeek-window-<pid>` (the mstsc process id). The HUD overlay is
on-demand — shown only by the `overlay` command, never automatically. Commands are newline- or
message-delimited UTF-8:

| command | effect |
|---|---|
| `title <text>` | append `<text>` to the window title (empty restores) |
| `move <x> <y> <w> <h>` | reposition / resize |
| `topmost on\|off` | toggle always-on-top |
| `show min\|max\|restore` | window state |
| `overlay <text>` | show/update the HUD banner (empty text hides it) |
| `flash` | flash the taskbar button |
| `foreground` | restore + pull the window to the front |
| `fullscreen` | foreground the window and ensure it's fullscreen (mstsc Ctrl+Alt+Break toggle, only if not already covering the monitor) — used by the switcher |

```powershell
# $pid here is the target mstsc's process id (the plugin logs its pipe name on load)
$pipe = "\\.\pipe\rdpeek-window-$mstscPid"
'title hello from the pipe' | Out-File -Encoding ascii $pipe
'overlay recording…'        | Out-File -Encoding ascii $pipe
'topmost on'                | Out-File -Encoding ascii $pipe
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
and a HUD overlay text box. It targets the connection **selected on the Overview tab** — deriving that
mstsc's pid from the RDP window and connecting to `\\.\pipe\rdpeek-window-<pid>` per command
(connect-write-close), off the UI thread, so a missing plugin never hangs the dashboard. The controls
grey out when no connection is selected.

**Switching between windows.** The Window tab has a *switcher sidebar* — a thin, borderless,
always-on-top strip docked to the left edge that lists every open RDP connection; click one to bring
its mstsc window forward **and make it fullscreen** (it sends `fullscreen` to that connection's pipe).
A small accent **peek handle** sits at the left edge (mid-screen) as a hint — hover it to reveal the
sidebar. The sidebar **auto-hides when it loses focus** and can also be revealed by shoving the cursor
into the left edge, or dismissed with its **Hide** button. A global **Ctrl+Alt+← / →** hotkey (toggle
on the same tab) cycles to the previous / next connection and switches to it. Everything shares the
dashboard's live connection list.

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
- The control pipe is **per mstsc process**: `\\.\pipe\rdpeek-window-<pid>`. Each in-process plugin
  serves its own pipe, so with several mstsc windows the viewer addresses a specific one by that
  window's pid (the Companion does this from the selected connection). One caveat: a single mstsc
  process hosting multiple tabbed sessions has one plugin instance / one pipe.
- `DllCanUnloadNow` returns `S_FALSE` on purpose (see Lifecycle): background threads outlive the COM
  objects, so the DLL must stay mapped rather than be unloaded from under them.
- Because the object is released after `Initialize`, mstsc keeps the DLL locked for the life of the
  process — to rebuild, close that `mstsc` first.
- x64 only, to match `mstsc`.
