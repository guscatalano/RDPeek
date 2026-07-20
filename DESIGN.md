# RDPeek — Detailed Design

Companion to [`ROADMAP.md`](ROADMAP.md) (phasing) and [`proto/diag.proto`](proto/diag.proto)
(wire contract). This document is the *how*: architecture, components, data flows,
build/registration, testing, and the acceptance criteria that define "done."

---

## 1. Goals / non-goals

**Goals**
- A **dev-first** DVC workbench: inspect live channels, read remote host/process
  info, transfer files, and stress/verify DVC plugins under development.
- Useful **today**, without the unreleased per-DVC perf counters; light them up
  automatically when they ship (~Aug 2026).
- Safe by construction: capability-negotiated, read-only-capable, audited.

**Non-goals**
- Not a general remote-admin/RMM product. No unattended access, no persistence
  as a service beyond the RDP session lifetime.
- Not a payload sniffer for *other* plugins' channels (isolated by design). We
  report metadata/health, and decode only channels we own.

---

## 2. Architecture

```
        LOCAL (RDP client machine)                REMOTE (RDP session on host)
  ┌───────────────────────────────────┐      ┌──────────────────────────────────┐
  │  mstsc.exe                         │      │  Rdpeek.Agent.exe (in-session)        │
  │   └─ loads Rdpeek.Plugin ◄───┼──────┼──► opens channels:               │
  │        (COM, LocalServer32)        │ DVC  │      dvc::diag::inspector         │
  │        - IWTSPlugin lifecycle      │      │      dvc::diag::files             │
  │        - listeners per channel     │      │   - session lifecycle (reconnect)│
  │        - Envelope codec            │      │   - collectors:                  │
  │             ▲ named pipe           │      │       SysInfo, Processes,        │
  │             │ (local IPC)          │      │       Counters(future), Files    │
  │  ┌──────────┴─────────────┐        │      │   - capability gating + audit    │
  │  │ Rdpeek.Viewer     │       │      └──────────────────────────────────┘
  │  │ (Electron + TS UI)      │       │
  │  │  dashboards, tables,    │       │      ┌──────────────────────────────────┐
  │  │  file transfer, logs    │       │      │  Rdpeek.Doctor (standalone) │
  │  └─────────────────────────┘       │      │   registry/COM/bitness checks     │
  │  registry AddIns + ETW (client)    │      │   (runs local; no channel needed) │
  └───────────────────────────────────┘      └──────────────────────────────────┘
```

**Three process roles on the client:** `mstsc` (hosts the plugin), the plugin's
`LocalServer32` process(es), and the Electron viewer. Plugin instances register
with a **local broker** (well-known named pipe); the viewer connects to the broker,
which aggregates every active RDP connection. **One process per host:** the agent.

> **Multiple concurrent connections** are a first-class case — see §5.6. The DVC
> layer isolates them for free (one channel/codec instance per connection); the
> broker keys and aggregates them for a single multi-connection viewer.

**Why LocalServer32:** a crash in our plugin cannot take down the user's
`mstsc.exe`, and we can launch the plugin process under a debugger. Recommended by
Microsoft for exactly this.

---

## 3. Technology choices (recommended)

| Layer | Choice | Rationale |
|---|---|---|
| Client plugin core | **.NET 8** (fork `Advanced/dotnet`) | Fast to build; sample provides `RdpPluginBase`, COM activation, Native AOT export options. |
| Remote agent | **.NET 8** | Shares the protocol library + collectors with the plugin; WTS P/Invoke is straightforward. |
| Wire format | **protobuf** (`Google.Protobuf`) | Matches the sample; `proto/diag.proto` already defined. |
| Viewer | **Electron + TypeScript** | Fastest path to real dashboards/tables/graphs; sample has an Electron variant. |
| Plugin ↔ viewer IPC | **Named pipe** + length-prefixed protobuf | Same codec as the channel; trivial to reuse. |
| Doctor | **.NET 8 console** | Standalone, no channel. |

> C++ alternative: swap plugin+agent for `Advanced/cpp`. Higher effort, lower-level
> WTS access, no GC pauses. Only worth it if the counter/ETW hot paths prove too
> heavy in .NET — unlikely at these data rates. **Recommendation: .NET 8.**

---

## 4. Solution layout

```
RDPeek/
├─ proto/
│  └─ diag.proto                     # wire contract (done)
├─ src/
│  ├─ Rdpeek.Protocol/         # generated protobuf + framing codec (LIBRARY)
│  │    ├─ FrameCodec.cs             # [4B LE len][payload], 16 MiB cap, reassembly
│  │    └─ EnvelopeRouter.cs         # request_id correlation, oneof dispatch
│  ├─ Rdpeek.Plugin/           # client COM plugin (LocalServer32)
│  ├─ Rdpeek.Agent/                      # in-session remote agent
│  │    ├─ Collectors/               # SysInfo, Processes, Counters, Files
│  │    └─ SessionLifecycle.cs       # WTSRegisterSessionNotificationEx state machine
│  ├─ Rdpeek.Viewer/           # Electron + TS
│  └─ Rdpeek.Doctor/           # registration diagnostics (standalone)
├─ tools/
│  ├─ register.ps1  unregister.ps1   # COM registration helpers
│  └─ new-dvc/                       # scaffolder (later)
├─ tests/
│  ├─ Protocol.Tests/                # framing/codec unit tests
│  └─ Conformance/                   # headless loopback + wire conformance
├─ DESIGN.md  ROADMAP.md  README.md
```

---

## 5. Component designs

### 5.1 Protocol library (`Rdpeek.Protocol`)
The reusable core every other component links against.
- **FrameCodec** — writes `[4-byte LE uint32 length][Envelope bytes]`; on read,
  buffers partial frames and emits complete `Envelope`s. Enforces the 16 MiB cap;
  rejects/records malformed length prefixes.
- **EnvelopeRouter** — assigns `request_id`s, matches responses to pending
  requests (`TaskCompletionSource` per id), dispatches unsolicited pushes
  (`request_id == 0`) to subscribers, stamps `utc_ticks`.
- Transport-agnostic: the same codec drives the DVC *and* the plugin↔viewer pipe.

### 5.2 Client plugin (`Rdpeek.Plugin`)
- Implements `IWTSPlugin` → `CreateListener` for `dvc::diag::inspector` and
  `dvc::diag::files`; per-connection `IWTSVirtualChannelCallback` feeds `FrameCodec`.
- On connect: sends `Hello`, awaits `Capabilities`, exposes them to the viewer.
- Hosts the **named-pipe server** the viewer connects to; bridges viewer requests
  → channel and channel pushes → viewer.
- **Client-side collectors** (no agent needed): registry `AddIns` enumeration +
  ETW (`Microsoft-Windows-TerminalServices-ClientActiveXCore`) for the client's
  channel roster.

### 5.3 Remote agent (`Rdpeek.Agent`)
- `SessionLifecycle`: `WTSRegisterSessionNotificationEx` + hidden message window;
  on `WTS_SESSION_LOGON`/`WTS_REMOTE_CONNECT` opens channels via
  `WTSVirtualChannelOpenEx`; on disconnect closes and waits — **survives
  reconnect** (regenerates transfer/subscription state).
- Two I/O modes (from the sample): synchronous `WTSVirtualChannelRead/Write`, and
  async file-handle mode (`WTSVirtualFileHandle` + overlapped `ReadFile/WriteFile`)
  for the higher-volume streams (files, counter bursts).
- **Collectors**, each gated by an advertised capability:
  - `SysInfo` — `RtlGetVersion`/registry, `GetTickCount64`, CPU/memory APIs,
    `WTSQuerySessionInformation`. One-shot + interval subscription.
  - `Processes` — `WTSEnumerateProcessesEx`/`WTSEnumerateSessions`; `TERMINATE`
    via `WTSTerminateProcess` **only if `process_kill` advertised**.
  - `Counters` — **runtime-probed**: PDH `PdhEnumObjects`/`PdhOpenQuery`; if the
    per-DVC set is absent, `Capabilities.counters=false` and this stays dark.
  - `Files` — chunked/windowed engine (see 6.3); path-root enforced; sha256.

### 5.4 Viewer (`Rdpeek.Viewer`)
Screens (greyed per advertised capability):
- **Host header** — live SysInfo (build/UBR/edition, uptime, CPU%, memory).
- **Channels** — client roster (registry+ETW) ‖ host roster; diff highlighted.
- **Health** — ping/echo RTT + throughput graph now; passive counter graphs when
  the counter capability lights up.
- **Processes** — sortable table; terminate action (guarded) when permitted.
- **Files** — dual-pane browse + push/pull with progress bars and resume.
- **Log** — merged client+agent timeline keyed by `request_id`.

### 5.5 Doctor (`Rdpeek.Doctor`)
Standalone, runs without a session. Walks `HKCU`/`HKLM ...\AddIns`, resolves each
CLSID's activation model + module path, checks **bitness** vs. `mstsc`, and does a
live `CoCreateInstance` smoke test reporting the real HRESULT. Emits a
pass/warn/fail report. Client-wide — **one report regardless of how many
connections are open** (registration is global).

### 5.6 Multiple concurrent connections (broker + ConnectionId)
Use case: several RDP sessions open at once (multiple `mstsc` windows / RDCMan),
possibly to different hosts. Handled as follows:

- **Wire layer: isolated for free.** Each RDP connection is a separate transport
  with its own `IWTSVirtualChannelManager`, `Initialize`, listener, and channel
  callback. One `FrameCodec`/`EnvelopeRouter` instance per connection *is* the
  boundary — connection A and B never mix. **No `connection_id` on the DVC
  `Envelope`** (the channel instance already scopes it).
- **Broker.** However COM activates plugin instances (`REGCLS_MULTIPLEUSE` shared
  process, or one per activation), each **registers its connections with a local
  broker** on a well-known pipe. The broker assigns a **`ConnectionId`** per RDP
  connection and labels it from the first `Capabilities`/`SysInfoSnapshot`
  (host name, session id, user).
- **Viewer.** Connects to the **broker**, not a single plugin. Presents a
  **connection switcher** + an "all connections" overview; every panel (host
  header, channels, health, processes, files, log) is scoped to the selected
  `ConnectionId`.
- **Scoping consequences.** `transfer_id` is unique *within* a `ConnectionId`, not
  globally. When the per-DVC counters ship they are per-session on each host, so N
  connections yield N independent counter streams the broker keeps separate.
- **IPC only.** The `connectionId` tag lives on the **plugin↔broker↔viewer pipe
  framing**, never on the DVC wire — keeping the on-channel contract identical
  whether one or twenty connections are open.

**COM activation decision:** register the class as `REGCLS_MULTIPLEUSE` so a single
plugin process hosts all connections and talks to the broker once — simpler
aggregation, one audit sink. (Falls back cleanly to per-activation processes that
each dial the broker if isolation is ever preferred.)

---

## 6. Key data flows

### 6.1 Handshake
```
plugin  ──Hello{proto_ver, client_build}──►  agent
plugin  ◄──Capabilities{flags, file_roots, max_chunk}──  agent
viewer  ◄── capabilities forwarded over pipe; UI enables/greys panels
```

### 6.2 SysInfo subscription
```
viewer→plugin→agent : SysInfoRequest{interval_ms=2000}
agent→plugin→viewer : SysInfoSnapshot   (every 2s, request_id=0)
... SysInfoRequest{interval_ms=0} stops it
```

### 6.3 File pull (windowed, resumable)
```
FileOpen{PULL, path, transfer_id}
  ← FileOpenResult{total_size, resume_from}          (or Error)
FileChunk × N   (agent sends up to WINDOW unacked chunks, ≤ max_chunk_bytes)
  → FileAck{offset}   (viewer advances window as chunks land durably)
FileClose{sha256, COMPLETE}   → viewer verifies digest
```
Runs on `dvc::diag::files` so it never starves the dashboard on the control
channel. Backpressure via the ack window keeps the DVC send queue from
overrunning.

### 6.4 Counter light-up (future)
```
agent startup: PdhEnumObjects → per-DVC set present?
  no  → Capabilities.counters=false   (Health panel stays on ping/echo)
  yes → Capabilities.counters=true; on CounterSubscribe, push CounterSample
```
No code change ships when the OS build lands — the flag flips at runtime.

---

## 7. Threading / IO model
- Plugin: COM callbacks marshal onto a single channel reader; codec + router are
  lock-guarded; pipe server on its own thread.
- Agent: one thread per open channel in sync mode; read/write thread pair per
  channel in file-handle mode. Collectors run on a pool; file transfer uses the
  file-handle channel to avoid blocking control traffic.
- All cross-thread handoff through bounded queues (backpressure visible, not
  hidden).

---

## 8. Security & capability model
- **Default read-only build:** ship with `process_kill`, `file_push` **off**;
  `file_pull` restricted to a diagnostics root. Flip flags to build a fuller
  admin variant deliberately.
- **Path roots enforced agent-side** — every file path canonicalized and checked
  under `file_roots`; reject with `PATH_NOT_ALLOWED`.
- **Audit log** (agent) — every file open and every `ProcessAction` recorded with
  timestamp, path/pid, and result.
- **Authorized-use framing** in README; no evasion/stealth features.

---

## 9. Build, packaging, registration
- `dotnet build` for all .NET projects; `npm run make` for the viewer.
- `tools/register.ps1` writes the `AddIns` key + CLSID `LocalServer32` pointing at
  `Rdpeek.Plugin.exe`; `unregister.ps1` reverses it. Fixed dev CLSID.
- Agent ships as a single self-contained EXE copied into the session (or run from
  a share) — no install required in-session.
- `README.md` documents the one-time client registration + how to launch the
  agent in the remote session.

---

## 10. Testing strategy
- **Unit** (`Protocol.Tests`): framing round-trip, partial-frame reassembly, 16 MiB
  rejection, malformed length prefix, router correlation.
- **Loopback** (`Conformance`): plugin and agent codecs wired in-proc (no RDP) —
  full handshake, sysinfo, a file transfer with an induced mid-stream truncation,
  and reconnect recovery. Runs headless in CI.
- **Manual/live**: real RDP session for lifecycle, ETW, WTS enumeration, and the
  counter re-probe after the OS build ships.

---

## 11. Milestones (maps to ROADMAP)
- **M0 — Protocol library + loopback green** (unit + in-proc conformance).
- **M1 — MVP**: plugin+agent handshake over real DVC; SysInfo live in viewer;
  Doctor diagnosing a broken registration; merged logs.
- **M2 — v1**: channel roster (both views), ping/echo health graph, process table,
  file pull/push, frame inspector.
- **M3 — counters light up** after OS build lands: re-probe, flip capability,
  passive dashboards.
- **M4 — dev power tools**: fault-injection proxy, mock/REPL, benchmark, lint,
  scaffolder.

---

## 12. Definition of Done — what you should expect

**When M1 (MVP) is done**, you can:
- Register the plugin, launch the agent in an RDP session, open the viewer, and
  see the **remote host header populate live** (Windows build/UBR/edition, uptime,
  CPU%, memory) and refresh on an interval.
- Run **Doctor** against a deliberately-broken registration (wrong-bitness DLL,
  bad path, unregistered CLSID) and get a correct pass/warn/fail with the real
  HRESULT.
- See client-half and agent-half events on **one correlated timeline**.

**When M2 (v1) is done**, you have the everyday tool:
- A **Channels** view showing the client's loaded plugins (registry+ETW) beside
  the host's active channels, with the diff (loaded-but-never-connected)
  highlighted.
- A live **Health** graph (RTT + throughput from the active probe) you can leave
  running across a disconnect/reconnect and watch recover.
- A **Processes** table of the remote session (read-only by default; guarded
  terminate when the build permits).
- **File pull/push** with progress, resume, and sha256 verification — grab a
  remote log or push a repro without leaving the tool.
- A **Frame inspector** that decodes your own channel's frames and flags bad
  length prefixes, oversized frames, and sequence gaps.
- **Multiple simultaneous RDP connections** in one viewer: a connection switcher
  lists every open session, each panel scoped to the selected connection, with an
  all-connections overview — connections appear/disappear live as you open/close
  `mstsc` windows.

**When M3 lands** (OS build with the counters, ~Aug 2026):
- The **Health** panel gains passive per-DVC counter graphs automatically — no
  rebuild, just the runtime capability flipping on after a 5-minute re-probe to
  confirm the set's shape (client/host, per-channel, naming).

**When M4 is done**, it's a full DVC dev workbench: a **fault-injection proxy**
(latency/drop/reorder/truncate/forced-reconnect) to harden your plugin before the
network does, a **mock/loopback + REPL** to develop either half in isolation and
run wire conformance in CI, a **benchmark bench**, an **MS-RDPEDYC lint**, and a
`new-dvc` **scaffolder**.

---

## 13. Risks
- **Counter shape unknown until release** — mitigated by runtime detection; zero
  code depends on them pre-M3.
- **Fault-injection proxy** is the heaviest component (MITM over two DVCs) —
  scoped to M4, not v1.
- **ETW client provider stability** across Windows builds — treat ETW as
  best-effort enrichment, with registry enumeration as the reliable floor.
- **File transfer contends with the channels under test** — mitigated by the
  dedicated `dvc::diag::files` channel + ack windowing.
```
