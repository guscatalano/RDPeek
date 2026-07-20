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
| `Rdpeek.Agent` — SysInfo + process collectors, agent core | ✅ collectors verified live; `serve` DVC transport built |
| `Rdpeek.Plugin` — client COM plugin (`IWTSPlugin`, LocalServer32) | ✅ built; COM activation + `IWTSPlugin` verified; live DVC round-trip pending an RDP session |
| `Rdpeek.Client` — client-side DVC configuration roster | ✅ done, verified live (`rdpeek-plugin channels`) |
| Viewer, per-DVC counters, file transport | ⬜ not yet |

The per-DVC **performance counters** RDPeek will consume are not yet released in
Windows (expected ~2026); the agent runtime-detects them and lights up the
dashboard when they land — nothing depends on them meanwhile.

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

## Layout

```
proto/            wire contract (diag.proto)
src/
  Rdpeek.Protocol/  framing codec + envelope router (shared library)
  Rdpeek.Agent/     in-session remote agent (collectors + core)
  Rdpeek.Doctor/    standalone registration diagnostician
tests/
  Protocol.Tests/   framing/router unit tests
  Conformance/      full contract over an in-proc loopback (no RDP)
```

## Safety

RDPeek enumerates remote processes and can transfer files — it's a diagnostics
agent riding RDP. It ships **read-only by default** (no process termination, no
file push) via capability negotiation, confines file access to advertised roots,
and is intended for **authorized** admin / diagnostics / development use.

## License

TBD.
