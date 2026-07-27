# RDPeek

A **dev-first RDP Dynamic Virtual Channel (DVC) workbench** — for debugging DVC
plugins and RDP sessions: see who's listening, inspect remote host/process info,
transfer files, measure channel health, and stress/verify your own DVC plugins
during development.

Built on top of Microsoft's
[rdp-dvc-plugin-samples](https://github.com/microsoft/rdp-dvc-plugin-samples).

> **DVC** here means **Dynamic Virtual Channel** (the RDP extensibility mechanism),
> not Data Version Control.

See [`INSTALL.md`](INSTALL.md) to build/run, [`DESIGN.md`](DESIGN.md) for the
architecture, and [`ROADMAP.md`](ROADMAP.md) for phasing.

## How it works

A DVC plugin has two halves, and RDPeek uses both vantage points:

- **Client plugin** — a COM object loaded by `mstsc.exe` (`LocalServer32` for crash
  isolation), which listens on the diagnostics channels and bridges to a viewer.
- **Remote agent** (`rdpeek-agent`) — runs inside the RDP session, opens the
  channels with `WTSVirtualChannelOpenEx`, and serves collectors (host info,
  processes, files, channel health).

Everything speaks one length-prefixed protobuf `Envelope` (see
[`proto/diag.proto`](proto/diag.proto)) over two channels:
`dvc::diag::inspector` (control) and `dvc::diag::files` (bulk transfer).

## Status

Early. What exists and is verified today:

| Component | State |
|---|---|
| `Rdpeek.Protocol` — framing codec + envelope router | ✅ done (unit + conformance tested) |
| `rdpeek-doctor` — plugin registration diagnostician | ✅ done (verified against the live registry) |
| `Rdpeek.Agent` — SysInfo + process collectors, agent core | ✅ collectors + `serve` DVC transport verified live end-to-end |
| `Rdpeek.Plugin` — client COM plugin (`IWTSPlugin`, LocalServer32) | ✅ **live DVC round-trip verified** — pulls the remote host's info over the channel |
| `Rdpeek.Client` — client-side DVC configuration roster | ✅ done, verified live (`rdpeek-plugin channels`) |
| `Rdpeek.Companion` — dashboard: agent status + live host header + remote process table | ✅ verified live — host info & processes stream from the agent over the DVC |
| Per-DVC traffic counters — agent collector + "DVC traffic" tab | ✅ built; perfmon source verified on build 26100 (reports "no channels open" off-host, as expected) |
| Full viewer/dashboard, file transport | ⬜ not yet |

### Per-DVC traffic

The per-DVC **performance counters** have shipped: Windows exposes a
`Remote Desktop Virtual Channel` counter set with one instance per open channel,
carrying bytes in both directions, RTT and bandwidth. The agent reads it — no
elevation required. Where it's absent, the agent falls back to summing the RDP
server's ETW write-flush events, the approach merged in from
[RDP_DVC_Watcher](https://github.com/guscatalano/RDP_DVC_Watcher) (send
direction only, partial, needs Administrator).

Both sources are **server-side**, so they only produce numbers from inside the
session — that is why they live in the agent. The client has no per-channel
equivalent: mstsc's own ETW providers report transport byte counts without ever
naming a channel. `rdpeek-doctor dvcprobe` re-checks that on any build.
See [`DESIGN.md`](DESIGN.md) §6.4 for the evidence.

## Build & test

Requires the .NET SDK (8/9/10).

```powershell
dotnet build RDPeek.slnx
dotnet test  RDPeek.slnx
```

## Try it now

The agent's collectors run anywhere (no RDP session needed) via `selftest`:

```powershell
dotnet run --project src/Rdpeek.Agent -- selftest
```

Diagnose DVC plugin registrations on this machine:

```powershell
dotnet run --project src/Rdpeek.Doctor
```

See how DVCs are configured on this client (registered plugins + built-in channels):

```powershell
dotnet run --project src/Rdpeek.Plugin -- channels
```

Watch per-channel traffic live. Run this **inside an RDP session** — the counters
are server-side, so on your own desktop it will correctly say no channels are open:

```powershell
dotnet run --project src/Rdpeek.Agent -- dvcwatch
```

Optional, client-side, needs Administrator — sample mstsc's own ETW providers to
see what this Windows build exposes:

```powershell
dotnet run --project src/Rdpeek.Doctor -- dvcprobe --discover --seconds 20
```

## Layout

```
proto/            wire contract (diag.proto)
src/
  Rdpeek.Protocol/  framing codec + envelope router (shared library)
  Rdpeek.Agent/     in-session remote agent (collectors + core)
  Rdpeek.Doctor/    standalone registration diagnostician
tests/
  Protocol.Tests/   framing/router unit tests
  Agent.Tests/      DVC traffic parsing + rate derivation
  Conformance/      full contract over an in-proc loopback (no RDP)
```

## Credits

The ETW per-channel traffic route is merged in from
[RDP_DVC_Watcher](https://github.com/guscatalano/RDP_DVC_Watcher) (MIT), which
first established that the RDP server fires a per-channel "write flush" trace
event carrying the channel name and payload size.

## Safety

RDPeek enumerates remote processes and can transfer files — it's a diagnostics
agent riding RDP. It ships **read-only by default** (no process termination, no
file push) via capability negotiation, confines file access to advertised roots,
and is intended for **authorized** admin / diagnostics / development use.

## License

TBD.
