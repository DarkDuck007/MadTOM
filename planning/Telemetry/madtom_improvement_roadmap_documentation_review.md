# MADTOM Architecture & Documentation Review

This review evaluates the MADTOM telemetry platform across its documentation, backend daemon architecture, protocols, and client user experience, followed by concrete implementation recommendations.

---

## 1. Documentation Review & Fixes

### 1.1 Structural Hygiene in `ui-and-config.md`
- **Table of Contents Anomaly**: The header contains an out-of-place link (`- [History timing diagnostics](#history-timing-diagnostics)`) placed above the primary document title and table of contents.
- **Heading Duplication**: The title `# UI Guide & Configuration Reference` appears twice in the introductory header block.
- **Section Alignment**: The `History timing diagnostics` section is located at the very end of the document, separated from the `Telemetry Graph Scopes & Resolution` section where graph performance settings are discussed.

### 1.2 Missing Operational Scenarios in Documentation
- **Transport Security on Public Endpoints**: The documentation shows reverse-push targets running over the open internet (e.g., `Sakura1@realiteam.art:50052`). A dedicated security subsection should document whether TLS/mTLS or pre-shared authorization tokens are required when binding ports on public IPs.
- **TSDB Lifecycle & Retention**: While the daemon disk spooling (`-max-spool-mb`) is documented, the central collector's Pebble TSDB retention policy (TTL, disk eviction, or automatic compaction) is not explicitly detailed.
- **Systemd Capability Sandboxing**: Binding default TWAMP port `862` as non-root requires `CAP_NET_BIND_SERVICE`. While mentioned in passing, the `madtom-collector.service` unit file should include `AmbientCapabilities=CAP_NET_BIND_SERVICE` and `CapabilityBoundingSet=CAP_NET_BIND_SERVICE` so operators do not default to running the collector as `root`.

---

## 2. Recommended Architectural Enhancements

### 2.1 Transport Layer: mTLS & WireGuard/Token Authentication
Currently, nodes communicate over plain gRPC TCP streams. For internal LANs this is acceptable, but reverse-push mode often spans public cloud providers.

#### Proposed Improvements:
1. **gRPC Pre-Shared Key (PSK) or JWT Metadata**:
   - Add a lightweight `-auth-token` flag to daemons and collectors.
   - All gRPC requests pass an `authorization: Bearer <token>` metadata header verified by a gRPC interceptor.
2. **Mutual TLS (mTLS) Support**:
   - Provide `-tls-cert`, `-tls-key`, and `-tls-ca` flags.
   - In push/pull/reverse-push modes, verify certificates to prevent unauthorized endpoints from spoofing node IDs or scraping process tables.

```
+------------------+         gRPC over TLS + PSK Token         +----------------------+
|  Remote Daemon   | ========================================> |   madtom-collector   |
| (Push / Rev-Push)|        Header: "authorization: Bearer ..."| (Pebble TSDB & Hub)  |
+------------------+                                           +----------------------+
```

---

### 2.2 Storage Engine: Long-Term Compaction & Rollup Aggregation
Currently, the collector scans raw Pebble TSDB keys and relies on LTTB / absolute-time bucket downsampling at query time. For multi-week or multi-month retention, raw 1Hz scans will degrade iterator performance.

#### Proposed TSDB Retention & Rollup Design:
1. **Background Pruning Worker**:
   - Implement an automated retention reaper in `madtom-collector` governed by `-retention-days` (e.g., default: 30 days).
   - Use Pebble's `DeleteRange(startKey, endKey)` during off-peak windows to atomically truncate expired key ranges without per-key tombstones.
2. **Rollup Tables (Downsampled Tiers)**:
   - **Tier 0 (Raw 1Hz)**: Retained for 48 hours.
   - **Tier 1 (1-Minute Rollups: Min/Max/Avg)**: Retained for 30 days.
   - **Tier 2 (1-Hour Rollups: Min/Max/Avg)**: Retained for 1 year.
   - Queries automatically route to the coarsest tier matching the requested time scope, avoiding million-record scans on 24h+ views.

---

### 2.3 Protocol & Telemetry Capabilities

#### 1. Correlated Clock Offset Tracking for TWAMP
RFC 5357 one-way latency calculations require synchronized clocks (`-twamp-clocks-synchronized=true`). If clocks drift even slightly, one-way numbers skew negative or appear artificially inflated.
- **Recommendation**: Add an automatic chrony/NTP offset probe to the daemon scraper (`/run/chrony-dhcp` or `ntp_gettime`).
- The daemon reports its local NTP offset in the heartbeat metadata. The collector uses this to display a warning badge (`Clock Drift: ±32ms`) if clocks are not sufficiently synchronized for one-way flight metrics.

#### 2. Replay & WAL Backlog Catch-Up Indicator
When a node comes online after an outage, it replays backlogged WAL segments.
- **Recommendation**: Include backlog status in the live gRPC event envelope (`BacklogRemainingBytes`, `BacklogReplayPercent`).
- On the dashboard, show a subtle progress ring or badge (`Replaying Backlog: 78%`) next to the node name so operators know the data being ingested is catching up to real time.

---

### 2.4 Desktop UI & Client Workflow Enhancements

```
+---------------------------------------------------------------------------------------+
| SYNCHRONIZED CROSSHAIR CURSORS (Active across all rows)                               |
| [ Chart 1: CPU Breakdown ]   --|-- Hover at 16:24:10 (CPU: 42.1%)                    |
| [ Chart 2: Memory Used   ]   --|-- Synchronized Line  (RAM: 2.14 GB)                  |
| [ Chart 3: Disk I/O      ]   --|-- Synchronized Line  (Write: 14.2 MB/s)             |
+---------------------------------------------------------------------------------------+
```

#### 1. Synchronized Crosshairs Across Charts
- In the **Metrics Tab**, hovering the cursor over any single chart should project a synchronized vertical crosshair line across all adjacent visible graphs at that exact timestamp.
- This immediately reveals correlations between metric events (e.g., a CPU spike aligning with a disk write burst and a TWAMP RTT jump).

#### 2. Alert Webhooks & Threshold Indicators
- Add simple rule triggers inside the UI or collector (e.g., `CPU > 90% for 2m`, `TWAMP Packet Loss > 5%`).
- Display an active alert counter badge on the top bar and support outbound webhook alerts (Discord, Slack, or generic HTTP POST) for lights-out monitoring.

#### 3. Graph Snapshot & CSV/JSON Export
- Add a camera / export icon to graph card headers:
  - **Copy Image to Clipboard**: Generates a high-res PNG of the active canvas for pasting directly into Slack, GitHub issues, or post-mortems.
  - **Export CSV/JSON**: Dumps the active downsampled point array for offline analysis in Python or spreadsheet tools.

---

## 3. Prioritized Implementation Roadmap

| Priority | Feature / Enhancement | Target Subsystem | Complexity |
|---|---|---|---|
| **P0** | Clean up `ui-and-config.md` TOC anomaly and heading duplication | Documentation | Low |
| **P0** | Document TLS / PSK authentication guidance for reverse-push | Docs / Go Transport | Medium |
| **P1** | Add Pebble TSDB background retention reaper (`-retention-days`) | `madtom-collector` | Medium |
| **P1** | Synchronized vertical crosshairs across all open chart rows | `MADTOM.Console` | Medium |
| **P2** | Systemd unit hardening (`AmbientCapabilities` for port 862) | Deployment / Systemd | Low |
| **P2** | WAL replay catch-up progress indicator in UI | Proto / Daemon / UI | Medium |
| **P3** | Chart image snapshot (`PNG`) and CSV export buttons | `MADTOM.Console` | Low |
| **P3** | Outbound Webhook Alerting (Slack/Discord/HTTP) | `madtom-collector` | Medium |