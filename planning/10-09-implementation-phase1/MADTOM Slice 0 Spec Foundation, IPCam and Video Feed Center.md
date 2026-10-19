# MADTOM Slice 0 Spec: Foundation, IPCam and Video Feed Center

*MADTOM planning · draft v0.1 · 9 October 2026 · proposal for review. Items marked **\[default\]** are my proposed choices; override any of them.*

## 1. Purpose

Prove the whole chain end to end with the smallest possible amount of code: a paired Android phone streams one camera to Video Feed Center, which shows a live preview and records chunked video with a proxy. While doing so, build only the shared foundation the slice needs (protocol definitions, local IPC and registry, local pairing), so that later plugins reuse it instead of reinventing it.

The slice is deliberately not the finished product. Every simplification below is chosen so that the real design (capability engine, multi-camera, outputs, compositor) can be added later without rewriting what is built now.

## 2. Scope

**In scope**

- Protocol definitions (protobuf/gRPC) with generated C# and Go code.
- Local IPC over Unix sockets and Windows named pipes, plus a directory-based capability registry.
- Local-only pairing and trust (no MADTOM Account server), designed to be upgraded to Account-issued trust later.
- IPCam on Android: one camera, one fixed profile, SRT output, live zoom as the single proof of source-side control.
- Video Feed Center: separate engine process plus Studio UI plugin. One SRT ingest, one preview tile, chunked recording of an original and a proxy rendition, asset manifest.
- Glass-to-glass latency measurement.

**Out of scope (deferred)**

- Capability engine, benchmarking, budget solver, adaptive bitrate.
- Multi-camera, multi-device, interface binding, bonding.
- Compositor, virtual cameras, other output protocols, node editor, docking polish.
- StorageHub, MADTOM Account, NoxAI, automation rules.
- Desktop/Pi/UVC sources (the ingest design allows them, but only SRT is built).

## 3. Acceptance criteria

| ID | Test | Pass condition |
| --- | --- | --- |
| AT1 | Pair a phone with the hub via QR code | Phone appears in the trust store with the granted scopes; second use of the same QR token is rejected |
| AT2 | Pair via manual PIN path | Both screens show the same 4-digit comparison code; mismatch aborts pairing |
| AT3 | Unpaired device tries to open a session | Connection refused at TLS level; event logged |
| AT4 | Stream 1080p30 for 30 minutes on Wi-Fi | No crash, no unrecoverable stall; preview continuously updating |
| AT5 | Recording | Original chunks roll about every 5 minutes; a proxy chunk set exists alongside; both play in a standard player |
| AT6 | Manifest | Every chunk set has a manifest whose renditions overlap in time within 3 seconds |
| AT7 | Zoom | Hub sends a zoom value; phone applies it on the sensor; reported state echoes the value actually applied (clamped if needed) |
| AT8 | Requested vs granted | The UI shows what the encoder was asked for and what it actually granted |
| AT9 | Network drop | Disconnect Wi-Fi for 30 s; session and stream recover on their own; a new chunk starts; the old one is closed cleanly |
| AT10 | OBS interop | An OBS SRT push to a manually configured listener (own passphrase) appears as a feed with no pairing involved |
| AT11 | Latency | Glass-to-glass number is measured and displayed with per-stage breakdown (see section 10) |

## 4. Architecture

Processes on the hub machine and the phone:

```
Phone (Android)                          Hub machine
+---------------------------+            +----------------------------------------+
| IPCam app (net10.0-android)|  control   | madtom-feedd (engine, headless)        |
|  - foreground service      |<==========>|  - registry entry + gRPC (UDS / pipe)  |
|  - Camera2 -> MediaCodec   | mTLS gRPC  |  - control endpoint (TCP, mTLS)        |
|  - TS muxer + SRT sender   |  (phone    |  - SRT listener -> ffmpeg ingest       |
+---------------------------+  dials out) |  - recorder (original + proxy)         |
            \                             |  - preview frames                      |
             \==== SRT media (TS) ======> +------------------+---------------------+
                                                              | local gRPC (UDS)
                                          +-------------------v--------------------+
                                          | MADTOM Studio: Video Feed Center plugin|
                                          |  (UI client of the engine)             |
                                          +----------------------------------------+
```

Key decisions:

- **The engine is a separate headless process \[default\].** The Studio plugin is only a UI client over the local socket. This costs a little extra work now but validates the local-socket architecture immediately, and means the engine can later run on a server or Pi while the UI runs elsewhere. It also keeps Feed Center usable without IPCam (OBS, section 9.5).
- **The phone dials out to the hub \[default\].** Phones roam and change IP; the hub is stable. The phone opens one long-lived bidirectional control stream, and the hub sends commands down it. No inbound server is needed on the phone.
- **Media is MPEG-TS over SRT \[default\].** TS is what FFmpeg and OBS speak natively, so ingest needs no custom code on the hub. SRT's latency and retransmission are configurable per profile.
- **Media keys travel over the authenticated control channel.** The hub generates a per-session SRT passphrase and stream ID and sends them in `StartStream`. Nobody configures SRT secrets by hand for paired devices.
- **No Studio contract changes are needed for the slice.** The plugin uses the existing `IPluginModule` lifecycle; all new behaviour lives in the engine and shared libraries.

## 5. Protocol definitions

### 5.1 Layout and rules

- Directory `proto/madtom/<area>/v1/*.proto` at the repository root. Areas for the slice: `core`, `pairing`, `video`.
- C# is generated into `MADTOM_DOTNET/src/Core/MADTOM.Protocol`; Go into `MADTOM_GOLANG/pkg/protocol`. The generated output is committed or built in CI \[default: built in CI, not committed\].
- Versioning: package suffix `v1`. Only additive changes inside a version (new fields, new RPCs). Breaking changes mean `v2` served side by side.
- Every service is a **capability**: ID `madtom.<area>.<name>.v<N>`, for example `madtom.video.feed.v1`.

### 5.2 Pairing (sketch)

```proto
syntax = "proto3";
package madtom.pairing.v1;

service Pairing {
  rpc Begin(BeginRequest) returns (BeginResponse);
  rpc Complete(CompleteRequest) returns (CompleteResponse);
}

message BeginRequest {
  string session_id = 1;
  bytes  device_cert_der = 2;   // device's self-signed P-256 cert
  string device_name = 3;
  string platform = 4;          // android, linux, windows, ...
  bytes  nonce_device = 5;
  bytes  token = 6;             // present on the QR path only
}
message BeginResponse {
  bytes hub_cert_der = 1;
  bytes nonce_hub = 2;          // both sides derive the comparison code
}
message CompleteRequest {
  string session_id = 1;
  bool   sas_confirmed = 2;     // manual path: user confirmed the codes match
}
message CompleteResponse {
  bool accepted = 1;
  repeated string granted_scopes = 2;
  string trust_id = 3;
}
```

### 5.3 Video source session (sketch)

```proto
package madtom.video.v1;

service VideoSourceControl {
  // Opened by the source toward the hub; the hub sends commands down the same stream.
  rpc Session(stream SourceMessage) returns (stream HubMessage);
}

message SourceMessage {
  oneof body {
    Hello hello = 1;
    CapabilityReport capabilities = 2;
    Ack ack = 3;
    Stats stats = 4;
    PropertyState state = 5;
    TimeProbe time_probe = 6;
  }
}
message HubMessage {
  oneof body {
    Welcome welcome = 1;
    StartStream start = 2;
    StopStream stop = 3;
    SetProperty set = 4;
    TimeReply time_reply = 5;
  }
}
message StartStream {
  string request_id = 1;
  string feed_id = 2;
  StreamProfile profile = 3;
  SrtTarget target = 4;
}
message SrtTarget {
  string host = 1; uint32 port = 2;
  string stream_id = 3; string passphrase = 4;
  uint32 latency_ms = 5;
}
message StreamProfile {
  string camera_id = 1;
  string codec = 2;             // "hevc" | "avc"
  uint32 width = 3; uint32 height = 4; uint32 fps = 5;
  uint32 bitrate_bps = 6;
  string rate_control = 7;      // "cbr" | "vbr"
  uint32 idr_interval_ms = 8;
  map<string,string> extra = 9; // free-form codec keys, never hardcoded
}
// Designed now to generalize later into the shared property model.
message Property {
  string id = 1;                // e.g. "camera.zoom_ratio"
  PropType type = 2;
  double min = 3; double max = 4; double step = 5;
  bool writable = 6;
  string unit = 7;
  double value = 8;
}
message Ack {
  string request_id = 1; bool ok = 2; string message = 3;
  StreamProfile granted = 4;    // what the encoder actually accepted
}
```

`CapabilityReport` in the slice carries only: cameras (ID, facing, supported size and fps list), encoders (name, hardware flag, codecs, profiles), and the list of `Property` descriptors (just `camera.zoom_ratio`). It is the seed of the full capability descriptor.

## 6. Local IPC and capability registry

- **Transport:** gRPC over Unix domain sockets (Linux, macOS, Android-internal) and named pipes (Windows). .NET supports both through custom connect callbacks; Go uses `unix://` dialing and a Windows pipe library.
- **Paths \[default\]:**
  - Linux: socket `$XDG_RUNTIME_DIR/madtom/<id>.sock`, manifest `$XDG_RUNTIME_DIR/madtom/manifests/<id>.json`.
  - Windows: pipe `\\.\pipe\madtom-<id>`, manifest `%LOCALAPPDATA%\MADTOM\run\<id>.json`.
  - macOS: the equivalent under the per-user temp directory.
- **Local trust:** sockets are mode 0600 (same user only); the OS user boundary is the trust boundary for local calls.
- **No central broker \[default\].** Each process writes its own manifest and removes it on clean exit; clients scan the directory and verify the PID is alive. This keeps plugins independent: there is no process everything else must wait for.
- **Manifest example:**

```json
{
  "id": "feedd",
  "version": "0.1.0",
  "pid": 4182,
  "endpoints": [
    { "transport": "uds", "address": "/run/user/1000/madtom/feedd.sock" },
    { "transport": "tcp-mtls", "address": "0.0.0.0:7443", "enabled": false }
  ],
  "provides": [
    { "capability": "madtom.video.feed.v1", "version": "1.0" },
    { "capability": "madtom.video.source-control.v1", "version": "1.0" }
  ],
  "consumes": []
}
```

- **Consuming by capability:** `MADTOM.Ipc` exposes `FindProvider(capabilityId)` and a watcher for providers appearing or disappearing. Plugins ask for a capability, never for a plugin name; if nothing provides it they fall back to their local default.
- **Remote use:** the same services are served on a TCP endpoint with mTLS (section 7). Disabled by default; enabling it is explicit per endpoint.

## 7. Pairing and trust (local mode)

### 7.1 Model

- Each device-user has one **identity**: an ECDSA P-256 keypair and a self-signed certificate **\[default: P-256, not Ed25519, for portability across .NET platforms and the Android Keystore\]**. On Android the private key lives in the hardware-backed Keystore where available.
- A **trust store** (JSON or SQLite in the shared MADTOM data directory) holds entries: certificate fingerprint, display name, platform, granted scopes, issuer (`local` now, `account` later), created and last-seen times.
- Scopes for the slice: `video.source.publish`, `video.feed.view`, `video.feed.control`.
- Trust evaluation sits behind an `ITrustProvider` interface, so the Account server can later contribute trust without changing callers.

### 7.2 QR path (primary)

The hub shows the QR code; the phone scans it with its camera.

```
Hub (Feed Center)                                   Phone (IPCam)
  create session: token(128-bit), expiry 5 min
  show QR: madtom://pair?host=..&port=..&fp=<hub cert sha256>&tok=<token>&sid=..
                                                    scan QR
                                                    open TLS to host:port, require cert
                                                    fingerprint == fp  (pinned from QR)
                                                    Begin(session_id, device cert, token, nonce)
  verify token (single use, not expired)
  store device fingerprint + scopes
  Begin response: hub cert, nonce
                                                    store hub fingerprint
                                                    Complete(...)
  both sides now use mTLS with mutual pinning
```

Because the QR carries the hub fingerprint, an active attacker on the LAN cannot substitute their own certificate. The token proves the phone is the one that scanned the code.

### 7.3 Manual path (fallback)

No QR, such as a headless device: the hub shows a 4-digit comparison code derived from both certificates and both nonces; the phone shows the same. The user confirms they match on both screens (Bluetooth-style numeric comparison). A mismatch aborts. This avoids implementing a custom password-authenticated key exchange.

### 7.4 Threat model for the slice

- **Covered:** passive and active LAN attackers, replayed or stolen QR tokens (single use, short expiry), rogue devices without a trust entry, plaintext media (SRT is keyed with the per-session passphrase).
- **Revocation:** deleting the trust entry denies the next connection; active sessions are closed immediately.
- **Not covered yet:** a compromised host OS, theft of an unlocked paired phone, and cross-device trust sync (that is the Account server's job).
- **Unauthenticated ingest** (OBS, section 9.5) is off by default and enabled per listener with its own passphrase, clearly marked as untrusted in the UI.

## 8. IPCam (Android)

### 8.1 Structure

- Project layout follows the plugin guide: `MADTOM.Plugins.IPCam` (shared logic, `IPluginModule`), a `net10.0-android` head, and a standalone `.App` runner.
- A **foreground service** with foreground service type `camera` owns the capture pipeline, so streaming survives the screen turning off. Required permissions: camera, foreground-service-camera, notifications.
- Settings and the stream profile come from a JSON file in the plugin's storage directory; nothing about resolution, bitrate or codec is hardcoded.

### 8.2 Pipeline (zero-copy)

1. Camera2 session targets the **input Surface of MediaCodec** directly, so frames never enter managed memory.
2. The encoder is created from the profile. Codec choice **\[default\]**: HEVC if a hardware HEVC encoder exists, otherwise H.264. Rate control, bitrate, frame rate and IDR interval come from the profile.
3. After configuration, the app reads back the encoder's actual output format and returns it as `granted` in the `Ack`. Anything the encoder ignored (B-frames, vendor keys, low-latency hints) is visible, not silent.
4. Encoded buffers are wrapped by a **minimal MPEG-TS muxer written in C#**. Android's `MediaMuxer` cannot write TS to a stream, so this is a small, well-defined component (PAT, PMT, PES, 188-byte packets, H.264/HEVC NAL handling) with its own unit tests.
5. TS packets go to an **SRT caller** using libsrt through the NDK. See spike S1.

### 8.3 Source-side control (slice proof)

- One live property: `camera.zoom_ratio`, applied on the device with `CONTROL_ZOOM_RATIO` (Android 11+) or `SCALER_CROP_REGION` on older versions. The reported value is the one the camera reports back, not the one requested.
- This exercises the full control round trip (descriptor, set, ack, state echo) that exposure, lens switching and sensor modes will reuse.

### 8.4 Session behaviour

- On start: dial the hub, `Hello`, `CapabilityReport`, wait for `Welcome`.
- Reconnect with backoff on failure; resume streaming automatically if the hub still wants this feed.
- Time probes every few seconds (four-timestamp exchange) give the hub a clock offset estimate. This is the forerunner of the shared time service.

## 9. Video Feed Center

### 9.1 Engine (`madtom-feedd`)

- .NET console host exposing `madtom.video.feed.v1` on the local socket and `madtom.video.source-control.v1` on the mTLS TCP endpoint.
- Responsibilities: pairing server, session handling, SRT listener management, feed lifecycle, recording, preview, manifests.
- Media work uses **FFmpeg as a subprocess \[default for the slice\]**: simplest to ship and isolates crashes. An in-process libav path is a later optimization for latency-critical tiles.

### 9.2 Ingest

- An SRT listener per feed (port from a configurable range; allocator is trivial in the slice). FFmpeg reads `srt://...` and demuxes the TS.
- Each feed has a state machine: `Waiting`, `Live`, `Stalled`, `Reconnecting`, `Stopped`, with a stale-frame indicator in the UI.

### 9.3 Recording

- One FFmpeg process per feed with two outputs from the same packets:
  - **Original**: stream copy (no re-encode) into segments of the configured length, default 5 minutes, written as MKV or TS **\[default: MKV, crash-tolerant\]**. Segments cut at the next keyframe after the target, so the IPCam profile must emit an IDR at least every 2 seconds.
  - **Proxy**: re-encoded low-resolution, low-bitrate rendition (default 640x360, about 1 Mbps) with forced keyframes at the segment boundary.
- Because the two renditions may cut up to a couple of seconds apart, **renditions are linked by time, not by chunk index** (a design rule for MediaCenter later).
- Output directory, segment length, proxy settings and retention are all configuration. Retention in the slice is a simple age and size limit; tiered archiving is deferred.

### 9.4 Asset manifest

One JSON sidecar per chunk set, the seed of the shared asset manifest:

```json
{
  "schema": "madtom.asset/1",
  "assetId": "3f2c...",
  "feedId": "phone-rear-1",
  "deviceTrustId": "t_91ab...",
  "startUtc": "2026-10-09T10:00:00.000Z",
  "endUtc": "2026-10-09T10:05:01.200Z",
  "clock": { "source": "control-probe", "offsetMs": -12.4 },
  "pipeline": { "requested": { }, "granted": { } },
  "renditions": [
    { "role": "original", "path": "cam1/2026-10-09_100000.mkv",
      "codec": "hevc", "width": 1920, "height": 1080, "fps": 30,
      "startUtc": "...", "endUtc": "...", "durationMs": 301200 },
    { "role": "proxy", "path": "cam1/proxy/2026-10-09_100000.mkv",
      "codec": "avc", "width": 640, "height": 360, "fps": 30,
      "startUtc": "...", "endUtc": "...", "durationMs": 300100 }
  ]
}
```

### 9.5 OBS and other generic sources

A feed can be created as an **unauthenticated SRT listener** with a manually set passphrase. OBS (or any SRT sender) pushes to it. These feeds go through the same ingest, recording and preview path and are flagged as untrusted. This proves Feed Center works without IPCam, as intended.

### 9.6 Studio plugin (UI)

- `IPluginModule` implementation whose main view talks to the engine over the local socket and shows: feed list, pairing dialog with QR and comparison code, one preview tile, per-feed status and stats, the requested-vs-granted panel, zoom control (generated from the property descriptor), and the recording status.
- Preview **\[default for the slice\]**: the engine decodes the proxy rendition and sends raw BGRA frames to the UI at about 640x360; the UI paints them into a bitmap. A GPU texture path is a later spike (S4), not slice work.
- Theming, scaling, lexicon and notifications use the existing host services; no new host contract is required.

## 10. Latency measurement

- **Method A (whole chain):** a clock card in the Feed Center UI displays a running millisecond counter. Point the phone at the monitor so its camera sees the card; the preview tile (on the same screen) shows the delayed card. The difference between the two counters is glass-to-glass latency. A helper screenshot-and-compare mode records the numbers.
- **Method B (per stage):** the phone sends per-frame capture UTC (sensor timestamp converted through the clock offset) in a timed-metadata stream inside the TS. The engine logs capture, receive, decode and display times per sampled frame, producing a stage breakdown.
- Both report into the UI and the log. Method A is required for AT11; Method B is a stretch goal.

## 11. Repository layout additions

```
proto/madtom/{core,pairing,video}/v1/*.proto
MADTOM_DOTNET/src/Core/MADTOM.Protocol/          (generated C#)
MADTOM_DOTNET/src/Core/MADTOM.Ipc/               (sockets, manifest, registry watcher)
MADTOM_DOTNET/src/Core/MADTOM.Identity/          (keys, certs, trust store, pairing client/server)
MADTOM_DOTNET/src/Core/MADTOM.Media.Contracts/   (asset manifest model, stream profile)
MADTOM_DOTNET/src/Plugins/IPCam/
    MADTOM.Plugins.IPCam/  MADTOM.Plugins.IPCam.Android/  MADTOM.Plugins.IPCam.App/
MADTOM_DOTNET/src/Plugins/VideoFeedCenter/
    MADTOM.Plugins.VideoFeedCenter/  MADTOM.Feed.Engine/  MADTOM.Feed.Daemon/  MADTOM.Plugins.VideoFeedCenter.App/
MADTOM_DOTNET/MadTOM.Tests/Core/{Ipc,Identity,Media}/
MADTOM_DOTNET/MadTOM.Tests/Plugins/{IPCam,VideoFeedCenter}/
MADTOM_GOLANG/pkg/protocol/                      (generated Go)
Documentation/Plugins/{IPCam,VideoFeedCenter}/README.md
```

Follow the existing plugin checklist: solution entries in `MADTOM.sln` and `MADTOM.slnx`, test projects, documentation, and a standalone runner for each plugin.

## 12. Work packages

Sizes: S = days, M = 1 to 2 weeks, L = several weeks (at hobby pace).

| WP | Work | Size | Depends on |
| --- | --- | --- | --- |
| S1 | Spike: libsrt on Android (evaluate an existing Android SRT binding first; otherwise NDK build per ABI plus P/Invoke), sending a test TS to FFmpeg on the hub | S | none |
| S2 | Spike: Camera2 to MediaCodec Surface, read back granted format, apply zoom | S | none |
| S3 | Spike: gRPC over Unix socket and Windows pipe from .NET, plus a Go client reading the same manifest and calling it | S | none |
| S4 | Spike: Avalonia raw-frame tile throughput at 640x360 and 1280x720 | S | none |
| WP1 | Protocol definitions and code generation for C# and Go | S | S3 |
| WP2 | `MADTOM.Ipc`: server and client helpers, manifest writer, registry watcher, tests | M | WP1 |
| WP3 | `MADTOM.Identity`: key and cert management, trust store, pairing server and client, QR and comparison code, tests | M | WP1 |
| WP4 | Minimal MPEG-TS muxer (H.264 and HEVC) with unit tests against FFmpeg demuxing | M | S1, S2 |
| WP5 | IPCam Android app: service, pipeline, session client, zoom, profile file | L | WP3, WP4 |
| WP6 | `madtom-feedd`: control endpoint, SRT listener, ingest, recorder, manifests, state machine | L | WP2, WP3 |
| WP7 | Feed Center UI: feed list, pairing dialog, tile, status, requested-vs-granted, zoom | M | WP2, WP6, S4 |
| WP8 | Latency tooling, OBS interop test, network-drop test, documentation | M | WP5, WP6, WP7 |

Suggested order: all four spikes first (they de-risk the slice), then WP1, WP2 and WP3, then WP4 and WP6 in parallel, then WP5 and WP7, and WP8 to close.

## 13. Test plan

- **Unit:** TS muxer round-trip through FFmpeg, manifest serialization, trust store, token single-use and expiry, comparison-code derivation, profile parsing.
- **Integration:** engine plus a simulated source (a synthetic test-pattern sender) so most tests need no phone; pairing against that source; recorder segmentation and manifest consistency.
- **Network impairment:** `tc netem` loss, jitter and drop scenarios against the synthetic source and the phone, covering AT4 and AT9.
- **Device matrix:** collect capability reports from every phone you test and keep them as fixtures; this is the start of the community device database.
- **Security:** negative tests for AT3 and replayed or expired tokens.

## 14. Decisions and risks

| Decision | Default | Alternative | Note |
| --- | --- | --- | --- |
| Media transport | SRT with MPEG-TS | RTSP | SRT gives configurable retransmission latency; RTSP can be added as an output later |
| Engine process | Separate headless host | In-process in Studio | Separate validates the IPC design and enables remote hosting |
| Cert algorithm | ECDSA P-256 | Ed25519 | Broadest platform and Keystore support |
| Media backend | FFmpeg subprocess | In-process libav | Faster to ship now; revisit for latency |
| Registry | Directory of manifests | Central broker | No single point of failure; plugins stay independent |
| Recording container | MKV | TS, fMP4 | MKV tolerates abrupt endings |
| Unauthenticated ingest | Off by default | On | Enabled per listener for OBS, flagged untrusted |
| Feed Center name | Placeholder | Argus, Mosaic | Does not block the slice |

**Main risks**

- **libsrt on Android** (build, ABI packaging, licensing review): mitigated by spike S1; fallback is RTP over UDP with the same TS payload.
- **Encoder differences between phones:** some will ignore requested settings; mitigated by read-back and the requested-vs-granted UI.
- **Foreground-service limits and OEM battery killers:** a known source of mysterious stops; plan an on-device diagnostics screen early.
- **Avalonia video tile performance:** CPU raw-frame path may cap resolution or frame rate; S4 measures it, and the slice only needs a small proxy tile.
- **Scope creep:** the foundation layers are tempting to generalize; keep to what AT1 to AT11 require.
