# MADTOM: SlideForge Control Center Plugin

The **SlideForge** plugin (`MADTOM.Plugins.SlideForge`) monitors and administers the **SlideForge** Telegram document-generation bot through the Go `slideforge-probe` daemon over TLS-pinned, Ed25519-authenticated gRPC.

---

## Architecture Overview

```
┌────────────────────────────────────────────────────────┐
│               MADTOM Studio (Avalonia C#)              │
│  - 7 Workspaces: Overview, Jobs, Users, Guides/Tips,   │
│    Sandbox, Config, Raw Logs & API                     │
│  - TOFU TLS Certificate Pinning (SfCertificatePins)    │
│  - Ed25519 Request Signing (fresh nonce per RPC)       │
│  - Allowlisted JSON-over-gRPC Client (SfRpc, 31 RPCs)  │
└───────────────────────────┬────────────────────────────┘
                            │ gRPC over TLS 1.3 (port 50057 / mDNS _slideforge-probe._tcp)
                            │ Headers: x-nox-auth-ts / x-nox-auth-nonce / x-nox-auth-sig
                            │ Streams: Zstd-compressed SfStateChunk & SfLogApiChunk
┌───────────────────────────▼────────────────────────────┐
│            slideforge-probe (Go System Daemon)         │
│  - Systemd Service: `slideforge-probe.service`         │
│  - Persistent Self-Signed TLS Identity + Replay Cache  │
│  - Allowlisted `Call` Passthrough + WAL Log Spool      │
└───────────────────────────┬────────────────────────────┘
                            │ Local Loopback TCP Framing (127.0.0.1:50058)
┌───────────────────────────▼────────────────────────────┐
│             SlideForge Python Bot Backend              │
│  - Jobs, Users/Quota, Guides/Tips, Sandbox, Config     │
└────────────────────────────────────────────────────────┘
```

---

## Plugin Metadata

| Property | Value |
|---|---|
| Plugin ID | `slideforge` |
| Display Name | `SlideForge` |
| Category | `AI` |
| OrderWeight | `125` |
| IconGlyph | `🎞️` |
| mDNS Service | `_slideforge-probe._tcp.local` (default port `50057`) |

---

## Workspaces

1. **Overview & Telemetry (`SfOverviewTabView`)**: Live active/queued/waiting job counters, 7-day job totals, failure rate, average duration, weekly quota burn, subsystem health (`bridge_ok`, `sandbox_ok`, `agy_ok`), top users, and a live `MetricHistoryChartControl` of active and queued jobs.
2. **Jobs (`SfJobsTabView`)**: Filterable, paginated job list (`list_jobs`) with live `SfJobEvent` updates, detail timeline, studio Q&A, Spec JSON viewer, job file download (`get_job_file` mode `"bytes"`), and Cancel / More Time actions.
3. **Users & Quota (`SfUsersTabView`)**: Searchable user list showing 7-day quota usage (`used / limit`), weekly limit override or reset-to-default (`set_user_quota`), and ban/unban (`set_user_banned`).
4. **Guides & Tips (`SfGuidesTabView`)**: Markdown file browser and monospace editor for `guides/*.md` (dirty-tracked Save), structured CRUD editor for `tips.yaml` (with JSON object validation for `when`), and an assembled prompt previewer (`preview_prompt`).
5. **Sandbox (`SfSandboxTabView`)**: Firecracker VM list (`sandbox_list_vms`), confirmed VM actions (`reset_session`, `hibernate`, `restart`, `kill`, `wipe`), and sandbox image status / rebuild (`sandbox_image_status`, `sandbox_rebuild_image`).
6. **Config (`SfConfigTabView`)**: Read-only pretty-printed view of the effective bot configuration (`get_config`, secrets already redacted by the bot).
7. **Raw Logs & API (`SfLogsTabView`)**: Bounded live log stream (5,000 entries) with level and text filtering, plus a live API/tool calls feed from `SubscribeLogsAndApi`.

---

## Security: TOFU TLS Pinning & Ed25519 Auth

- **TOFU Pinning (`SfCertificatePins`)**: Certificates are never blindly accepted. On first connection to a `host:port` endpoint, the UI displays the SHA-256 fingerprint and prompts the operator to compare it against the probe's journal line (`TLS certificate SHA-256: <hex>`) before clicking **Trust & Pin**.
- **Mismatch Protection**: If a previously pinned endpoint presents a different certificate, the connection is blocked and both fingerprints are displayed until the operator explicitly clicks **Forget Pin**.
- **Fail-Closed Store**: Pins are stored atomically as JSON in `%LocalAppData%/MADTOM/slideforge-pins.json`. A corrupt pin file fails closed on every evaluation.
- **Ed25519 Signing (`SfAuthTokenGenerator`)**: Every gRPC call is signed with a fresh GUID nonce and UTC timestamp (`x-nox-auth-ts`, `x-nox-auth-nonce`, `x-nox-auth-sig`).

---

## Setup

1. **Generate an Ed25519 client key**:
   ```bash
   ./MADTOM_GOLANG/bin/slideforge-probe -gen-key
   ```
   Save the emitted `SLIDEFORGE_CONTROL_KEY=<key>` token for MADTOM Studio, and place the corresponding public key in an allowlist file on the probe host (for example `/etc/slideforge-probe/authorized_keys`).

2. **Start `slideforge-probe` with `-authorized-keys`**:
   ```bash
   ./MADTOM_GOLANG/bin/slideforge-probe \
     -listen :50057 \
     -bot-host 127.0.0.1 \
     -bot-port 50058 \
     -state-dir /var/lib/slideforge-probe \
     -authorized-keys /etc/slideforge-probe/authorized_keys
   ```
   Note the certificate fingerprint printed in the probe log or systemd journal:
   ```text
   [SlideForge_Probe] TLS certificate SHA-256: <hex>
   ```

3. **Connect from MADTOM Studio (or `MADTOM.Plugins.SlideForge.App`)**:
   - Open the SlideForge plugin **Settings** modal and add a node.
   - Enter endpoint `127.0.0.1:50057` when forwarding the port over an SSH `LocalForward` (e.g. `ssh -L 50057:127.0.0.1:50057 user@node.example.com`), or enter the probe host's LAN address and port (`<host>:50057`).
   - Paste the client authorization key generated in step 1.
   - On first connection, compare the SHA-256 fingerprint shown in the trust prompt against the `TLS certificate SHA-256: <hex>` line from the probe logs, then click **Trust & Pin**.

---

## See Also

- [`slideforge-probe` — SlideForge Control Daemon](../../cli-and-scripts.md#12-slideforge-probe--slideforge-control-daemon)
- [`MADTOM.Plugins.SlideForge.App` — Standalone SlideForge Control Center](../../cli-and-scripts.md#13-madtompluginsslideforgeapp--standalone-slideforge-control-center)
