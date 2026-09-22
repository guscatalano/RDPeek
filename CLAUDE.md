# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**RDPeek** — a dev-first RDP **Dynamic Virtual Channel (DVC)** workbench for
debugging DVC plugins and RDP sessions. ("DVC" here is the RDP extensibility
mechanism, **not** Data Version Control — the repo folder is named `DVC_Tools`
but everything inside is RDPeek.) Built on Microsoft's
[rdp-dvc-plugin-samples](https://github.com/microsoft/rdp-dvc-plugin-samples).

Windows-only, x64. The authoritative design docs are `DESIGN.md` (architecture,
data flows, the counter/measurement asymmetry evidence), `ROADMAP.md` (phasing
M0–M4), and `proto/diag.proto` (the wire contract). Read `DESIGN.md` §6.4–6.5
before touching anything about traffic counters or link quality.

## Build, test, run

Requires the .NET SDK (9.0.200+ to parse the `.slnx` format; 10.x recommended).
Everything targets `net8.0` / `net8.0-windows`.

```powershell
dotnet build RDPeek.slnx
dotnet test  RDPeek.slnx
```

Run one test project / a single test:

```powershell
dotnet test tests/Protocol.Tests
dotnet test tests/Protocol.Tests --filter "FullyQualifiedName~FrameCodec"
```

Run the tools without a live RDP session (see `README.md` for the full list):

```powershell
dotnet run --project src/Rdpeek.Agent  -- selftest    # collectors, no DVC
dotnet run --project src/Rdpeek.Agent  -- dvcwatch    # live per-channel traffic (run on the session host)
dotnet run --project src/Rdpeek.Doctor                # diagnose plugin registrations
dotnet run --project src/Rdpeek.Plugin -- channels    # this client's DVC config
```

CI (`.github/workflows/ci.yml`) builds + tests on `windows-latest` and publishes
self-contained single-file `rdpeek-agent.exe` / `rdpeek-doctor.exe`. `release.yml`
attaches those to a GitHub release on a `v*` tag.

## Architecture

Two vantage points on the same DVC, both speaking one length-prefixed protobuf
`Envelope`:

- **Client plugin** (`Rdpeek.Plugin`) — a COM object (`IWTSPlugin`) loaded by
  `mstsc.exe` via **`LocalServer32`** (out-of-process, so a plugin crash can't
  kill `mstsc`). Listens on the diagnostics channels; bridges to a viewer over a
  local named pipe.
- **Remote agent** (`rdpeek-agent`) — runs *inside* the RDP session, opens the
  channels with `WTSVirtualChannelOpenEx`, and serves collectors (SysInfo,
  processes, network/sessions/services, perf, files). Survives disconnect/reconnect.

Two channels: `dvc::diag::inspector` (control + diagnostics) and
`dvc::diag::files` (bulk transfer, isolated so a large file can't starve the
dashboard). `request_id` correlates responses; `request_id == 0` is an
unsolicited push (subscriptions).

### Project dependency graph

```
Rdpeek.Protocol   (library: generated protobuf + FrameCodec + EnvelopeRouter)
   ├── Rdpeek.Client   (Broker/BrokerServer pipe, ClientChannels, RdpWindows)
   │      ├── Rdpeek.Plugin          (client COM plugin, LocalServer32)
   │      ├── Rdpeek.Doctor          (registration diagnostician + client probes)
   │      └── Rdpeek.Companion.WinUI (dashboard UI; also refs Protocol)
   └── Rdpeek.Agent    (in-session agent, collectors)
```

`Rdpeek.Client` is deliberately UI-framework-free so both the WinUI Companion and
the headless Doctor reuse the broker and RDP-window detection.

### Things that require reading several files to understand

- **The wire contract is generated, never hand-written.** `proto/diag.proto` is
  compiled to C# at build time by `Grpc.Tools` inside `Rdpeek.Protocol` (namespace
  `Dvc.Diag.Protocol`). To change the protocol, edit the `.proto` and rebuild —
  do not look for or edit generated message classes. `Rdpeek.Protocol` is the only
  project that references the proto.

- **One codec, two transports.** `FrameCodec` (`[4-byte LE length][Envelope]`,
  16 MiB cap, partial-frame reassembly) and `EnvelopeRouter` (request/response
  correlation, push dispatch) drive **both** the DVC channel *and* the
  plugin↔broker↔viewer named pipe. Keep them transport-agnostic.

- **The broker aggregates connections; `ConnectionId` lives only on the IPC
  framing, never on the DVC wire.** The well-known pipe is `rdpeek-broker`
  (`Broker.PipeName` in `src/Rdpeek.Client/Broker.cs`). Each RDP connection is
  already isolated at the DVC layer (one channel/codec instance per connection),
  so the on-channel `Envelope` carries no connection id; the broker keys and
  labels connections for a single multi-connection viewer. Don't add a
  `connection_id` to the DVC `Envelope`.

- **Server-side vs. client-side measurement is an intentional asymmetry, not an
  omission.** No RDP perfmon counter set is owned by a client binary (`DESIGN.md`
  §6.4 has the per-provider evidence), so all traffic/link counters are read by
  the agent on the session host and merely relayed to the client. The one
  genuinely two-sided number is the plugin timing its *own* channel round-trips
  with a local stopwatch and counting its own bytes (`ClientLink`, IPC-only) —
  shown next to the agent's numbers so the gap reads as DVC queueing vs. network.

- **Per-DVC traffic has two sources, probed at runtime.** Primary: the
  `Remote Desktop Virtual Channel` perfmon set (both directions, no elevation).
  Fallback where absent: ETW `write-flush` events summed per channel (send
  direction only, needs Administrator), ported from
  [RDP_DVC_Watcher](https://github.com/guscatalano/RDP_DVC_Watcher). The scraping
  lives in `DvcTrafficParser` so it stays unit-testable without a trace session
  (that is what `tests/Agent.Tests` covers).

- **Capability negotiation gates everything, read-only by default.** The agent
  advertises `Capabilities` after `Hello`; the viewer greys out anything not
  advertised. Destructive/ writing features (`process_kill`, `file_push`) ship
  **off**; file access is confined to advertised `file_roots` and paths are
  enforced agent-side. Preserve this posture when adding collectors or actions.

### Platform / build gotchas

- `Rdpeek.Plugin` and `Rdpeek.Companion.WinUI` are pinned to **x64** (Doctor and
  Agent too) — Doctor must match `mstsc`'s bitness for its `CoCreateInstance`
  smoke test to mean anything.
- The plugin uses **built-in COM interop** (CCW via `CoRegisterClassObject`),
  `EnableComHosting=false`. It runs as a COM server only when launched with
  `-Embedding` (by `mstsc`); other args are dev subcommands.
- The Companion is WinUI 3 / Windows App SDK, self-contained
  (`WindowsAppSDKSelfContained=true`) so it launches without the runtime
  pre-installed; MVVM via `CommunityToolkit.Mvvm`.

## Registration & deployment (Windows-specific, no admin needed)

- `tools/register.ps1` / `unregister.ps1` — client plugin COM registration
  (per-user HKCU `LocalServer32` + Terminal Server Client `AddIns\RDPeek`).
  `-Machine` (elevated) for a machine-wide registration.
- `tools/install-agent-web.ps1` / `uninstall-agent-web.ps1` — one-liner that
  registers a **scheduled task triggered on RDP connect** to auto-start
  `rdpeek-agent serve` in the session. Auto-start is via the scheduled task by
  design (not keystroke/mstscax injection).
