# RDPeek — Roadmap

A dev-first Dynamic Virtual Channel (DVC) plugin for debugging RDP virtual
channels: inspect who's listening, read per-channel performance counters, pull
remote host/process info, transfer files, and stress/verify your own DVC plugins
during development.

Built on top of [microsoft/rdp-dvc-plugin-samples](https://github.com/microsoft/rdp-dvc-plugin-samples)
(fork the **Advanced** C++ or .NET 8 sample for the base plumbing).

Wire contract: [`proto/diag.proto`](proto/diag.proto).

- **Client plugin** — COM object loaded by `mstsc.exe` (`LocalServer32` activation
  for crash isolation + debuggability).
- **Remote agent** — runs inside the RDP session; opens the channel with
  `WTSVirtualChannelOpenEx`; survives disconnect/reconnect.
- **Viewer** — UI over the client plugin (Electron sample is the fast path).

Channels:
- `dvc::diag::inspector` — control + diagnostics
- `dvc::diag::files` — bulk file transfer (isolated so a big transfer can't
  starve the live dashboard)

---

## Cross-cutting (present from day one, not a phase)

This tool enumerates remote processes, transfers files, and can terminate
processes — it's a management agent riding RDP. Bake the safety model in from the
first commit, not later:

- **Capability negotiation** — agent advertises only what its build allows
  (`Capabilities` in the proto). Viewer greys out the rest. Ship a **read-only
  build** by simply omitting write/exec flags.
- **Path roots** — file transfer confined to advertised `file_roots`; agent
  rejects anything outside.
- **Audit log** — every file touched and every action (esp. `TERMINATE`) logged
  on the agent side.
- **Authorized-use framing** — this stays in the admin/diagnostics/CI lane.

---

## MVP — "prove the pipe + kill the #1 dev pain"

Goal: end-to-end structured request/response over a real DVC, plus the single
most valuable standalone dev tool.

| Item | Why | Notes |
|---|---|---|
| **Reusable protocol/framing library** | Everything else consumes it | `[4-byte LE len][Envelope]`, 16 MiB cap, one `Parse`+switch per channel. Ship as a library, not baked into the app. |
| **Client plugin + agent skeleton** | The base | Fork Advanced sample; wire `Hello`/`Capabilities` handshake. |
| **SysInfo (one-shot + subscribe)** | Proves bidirectional structured data | Windows build/UBR/edition, uptime, CPU model/%, memory, host/user/session. Cheap sources, no extra privilege. |
| **Registration doctor** | #1 dev pain; mostly local, no channel needed | Walk `AddIns`, resolve CLSID + activation model, **bitness check** (x86 DLL under x64 mstsc), `CoCreateInstance` smoke test → real HRESULT. |
| **Correlated logging** | Debuggability floor | Merge client-half + agent-half logs on one timeline keyed by `request_id`. |

**Done when:** you connect, see the remote host header populate live, and
`registration doctor` correctly diagnoses a deliberately-broken registration.

---

## v1 — "the core debugging tool"

Goal: the everyday inspector an RDP/DVC engineer would actually keep open.

| Item | Why | Notes / dependencies |
|---|---|---|
| **Channel health (ship first, counter-independent)** | The per-DVC perf counters aren't released yet (~Aug 2026) — v1 must be useful without them | Derive health from the **active ping/echo probe** (RTT, throughput) + **ETW** open/close/flow events. This is the backbone until the counters land. |
| **Per-DVC counter dashboard (light up when shipped)** | Richer, passive channel health once the counters exist | **Runtime-detected**, never assumed: agent probes for the counter set via PDH and sets `Capabilities.counters`; viewer greys the panel until then. Collector runs wherever the counters actually appear (client and/or host — TBD, see open decisions). Relayed via `CounterSample` if host-side. |
| **Channel roster + reconciliation** | Two views: what the client loaded vs. what the host sees | **Client view** = registry `AddIns` + ETW (`Microsoft-Windows-TerminalServices-ClientActiveXCore`). **Host view** = agent's process/session enum (and counter instances once available). Diff = plugins that loaded client-side but never connected server-side. |
| **Correlation view** | The killer view | Per-channel bytes/backlog next to `User Input Delay per Session` + CPU on one timeline. |
| **Remote process/session list** | "processes from the remote machine" | `WTSEnumerateProcessesEx` / `WTSEnumerateSessions`. Read-only by default; `TERMINATE` gated behind `process_kill` capability + confirm. |
| **File transfer** | Pull logs/dumps off the box; push repros | Chunked + **windowed** (N unacked chunks), own channel, pull+push, per-chunk offset + whole-file `sha256`, resume from last `FileAck`. |
| **Frame inspector / channel tap** | "why is my message malformed" | For channels you own: per-frame timestamp/dir/size + decoded protobuf field tree; flags bad length prefix, oversized frames, sequence gaps. |
| **Viewer UI** | Make it usable | Electron sample: live process table, counter graphs, host header, transfer progress. |

**Done when:** you can watch a channel back up in real time, correlate it with an
input-delay spike, pull the remote log that explains it, and decode the offending
frames — without leaving the tool.

---

## M2 — "the RDPeek companion app (no embedded RDP)"

Goal: keep plain `mstsc.exe` + the DVC plugin; add a native client-side **companion
app** (WPF/WinForms, cohesive with the .NET solution) that is both the dashboard/
viewer and the bootstrap helper. No RDP-control hosting (`mstscax`) — the companion
drives the existing mstsc window from outside via Win32 input injection.

| Item | Why | Notes |
|---|---|---|
| **Companion app + pipe link** | UI without hosting RDP | Connects to the plugin/broker over the local named pipe; shows connection state, roster, host header, log. Multi-connection per §5.6. |
| **One-click agent bootstrap** | "Server-side host not detected — copy & run?" | Plugin detects no `OnNewChannelConnection`; companion offers the button. On click: set clipboard to the bootstrap command, `SetForegroundWindow(mstsc)` → `SendInput` **Win+R** → **Ctrl+V** → **Enter** (paste via CLIPRDR is far more reliable than typing). Command copies `\\tsclient\…\rdpeek-agent.exe` to remote `%TEMP%` and runs `serve`. Verify RDPDR + CLIPRDR via the client roster first; save/restore clipboard; best-effort focus. Operator-initiated automation of one's own authorized session — not remote-exec. |
| **Live dashboard** | The payoff | Host header, channel roster (client ‖ host), processes, transfers, log — over the pipe from the plugin. |

Trade-off vs. an embedded RDP control: input injection is more focus/timing/window-
targeting sensitive, but hosts nothing and keeps the user's normal mstsc.
Prereq: a trimmed/ReadyToRun agent publish to shrink the 65 MB self-contained exe.

---

## Later — "dev-first power tools"

Goal: turn it from an inspector into a full DVC development workbench. Each is
independent; sequence by demand.

| Item | Why | Notes |
|---|---|---|
| **Fault injection / chaos proxy** | Test robustness before the network does | MITM relay over two DVCs: latency/jitter, drop/reorder/dup/**truncate** frames, oversized frames, forced mid-transfer reconnect. Heaviest piece (needs its own relay design) — decide scope deliberately. |
| **Mock / loopback harness** | Develop one half in isolation | Mock agent + mock client; in-proc loopback (no RDP) for fast unit tests of message handlers. |
| **Interactive REPL + headless CLI** | Hand-drive + CI conformance | Send arbitrary `Envelope`s, see decoded replies. Same engine headless → assert round-trip/framing/reconnect on every build. |
| **Benchmark bench** | Numbers when tuning | Payload/window/chunk sweeps; WTS-API mode vs. file-handle async I/O; emit a table. |
| **MS-RDPEDYC conformance lint** | Works beyond mstsc | Channel-name length, PDU size, framing checks against the spec. |
| **Scaffolding generator** | `new-dvc <name>` | Emits starter client + agent + `.proto` wired to framing + handshake, pre-registered under a dev CLSID. |
| **Debugger ergonomics** | Faster inner loop | `LocalServer32` launch-under-debugger wrapper (`-Embedding` → jit-attach). |

---

## Open decisions to resolve early

1. **Counter set — not released yet (~Aug 2026); do not block on it.**
   Probed this client during an active RDP session on 2026-07-19: no per-DVC
   counter set registered, and `RemoteFX Network`/`Graphics` had zero live
   instances. That reflects the **feature not having shipped**, *not* where it
   will live — draw no conclusion about client-vs-host from a pre-release box.
   Plan: v1 ships channel health via the **ping/echo probe + ETW**; the counter
   dashboard is **runtime-detected** (`Capabilities.counters`) and lit up when the
   build lands. **Re-probe after release** to answer: client-side, host-side, or
   both? per-channel instanced? named by channel? — those decide the collector's
   final home (viewer-local vs. relayed via `CounterSample`; the proto supports
   either).
2. **Base language** — C++ vs. .NET 8 advanced sample for the agent.
3. **Fault-injection proxy** — v1 scope or explicit follow-on? (It's the heaviest
   single component.)
