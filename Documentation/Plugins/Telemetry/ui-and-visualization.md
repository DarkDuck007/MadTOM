# MADTOM Telemetry: UI & Visualization Guide

This guide covers the **MADTOM Telemetry Plugin** (`MADTOM.Plugins.Telemetry`) and its standalone application (`MADTOM.Plugins.Telemetry.App`). It explains the fleet dashboard, real-time and historical telemetry charts, time scopes, downsampling, process monitoring, TWAMP latency radar, diagnostic panels, and persistent configuration stores.

---

## Table of Contents

- [MADTOM Telemetry: UI \& Visualization Guide](#madtom-telemetry-ui--visualization-guide)
  - [Table of Contents](#table-of-contents)
  - [Overview \& Standalone Mode](#overview--standalone-mode)
  - [Fleet Dashboard](#fleet-dashboard)
    - [Live 1Hz Sparklines \& Health Badges](#live-1hz-sparklines--health-badges)
    - [Dynamic Node Grouping \& Fleet Filtering](#dynamic-node-grouping--fleet-filtering)
    - [Node Group Persistence (`node-groups.json`)](#node-group-persistence-node-groupsjson)
  - [Host Deep Dive \& Metrics Tab](#host-deep-dive--metrics-tab)
    - [Instant View Transition \& Graph Loading State](#instant-view-transition--graph-loading-state)
    - [Pinned Slim Control Bar](#pinned-slim-control-bar)
    - [Telemetry Graph Scopes](#telemetry-graph-scopes)
    - [Custom Scope (Date \& Time Picker)](#custom-scope-date--time-picker)
    - [Resolution-Adaptive Downsampling](#resolution-adaptive-downsampling)
    - [Graph Navigation: Zoom, Pan \& Page Scrolling](#graph-navigation-zoom-pan--page-scrolling)
    - [Custom Graph Layouts \& Presets](#custom-graph-layouts--presets)
    - [Multi-Device Disk I/O Metrics](#multi-device-disk-io-metrics)
  - [Collector \& Fleet Management](#collector--fleet-management)
    - [Adding, Editing, and Removing Collectors](#adding-editing-and-removing-collectors)
    - [Collector Persistence (`collectors.json`)](#collector-persistence-collectorsjson)
    - [Opt-In Global Metrics \& Top-Bar Pinning](#opt-in-global-metrics--top-bar-pinning)
    - [Node Opt-In Settings \& Safe Apply Workflow](#node-opt-in-settings--safe-apply-workflow)
  - [Process Monitoring \& Storage Modes](#process-monitoring--storage-modes)
    - [Three-Tier Collection Policy](#three-tier-collection-policy)
    - [Top-N Process Count Configuration](#top-n-process-count-configuration)
    - [Processes Manager: Sub-Tabs \& Breakdown Charting](#processes-manager-sub-tabs--breakdown-charting)
  - [TWAMP Flight \& Latency Radar](#twamp-flight--latency-radar)
  - [System Journal Log Streaming \& Virtual Viewer](#system-journal-log-streaming--virtual-viewer)
    - [Three-Tier Opt-In Policy](#three-tier-opt-in-policy)
    - [Monospace Virtual Log Viewer (`VirtualLogViewControl`)](#monospace-virtual-log-viewer-virtuallogviewcontrol)
    - [Client Cache \& Performance Settings](#client-cache--performance-settings)
  - [Diagnostics \& Performance Tuning](#diagnostics--performance-tuning)
    - [Background Ingestion Worker \& Coalesced UI Drains](#background-ingestion-worker--coalesced-ui-drains)
    - [Zero-Allocation Sparklines \& Deferred Pruning](#zero-allocation-sparklines--deferred-pruning)
    - [Client History Cache \& Off-Lock Decompression](#client-history-cache--off-lock-decompression)
    - [Compression Diagnostics Window](#compression-diagnostics-window)
    - [History Timing Diagnostics](#history-timing-diagnostics)
    - [Deep-Dive Transition Timing Diagnostics](#deep-dive-transition-timing-diagnostics)
    - [UI Performance Baselining](#ui-performance-baselining)

---

## Overview & Standalone Mode

The Telemetry module provides full-fidelity Linux fleet and node observability:
- **Integrated Plugin**: Loaded inside `MADTOM.Console` under the primary telemetry tab.
- **Standalone App**: Can run independently as `MADTOM.Plugins.Telemetry.App` without the console shell:
  ```bash
  dotnet run --project MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry.App/MADTOM.Plugins.Telemetry.App.csproj
  ```

---

## Fleet Dashboard

The Fleet Dashboard displays all monitored Linux nodes across all connected collector hubs.

### Live 1Hz Sparklines & Health Badges

- **Health Status**: Nodes update at 1Hz with color-coded health badges (Online, Degraded, Offline, Unknown).
- **Dual Sparklines**: Live micro-charts display moving 60-second CPU and Memory trends directly on each node card.
- **Hardware Specs**: Cards display architecture (`x86_64`, `aarch64`, `armv7`), logical core count, total RAM, and active kernel version.

### Dynamic Node Grouping & Fleet Filtering

Operators can group nodes dynamically by environment, datacenter, or role (e.g. `Production`, `US-East`, `Databases`):
- Click **Edit Groups** to create, rename, or delete groups.
- Filter the fleet dashboard with one click by toggling group filter pills.

### Node Group Persistence (`node-groups.json`)

Saved groups persist to:
```bash
~/.local/share/MADTOM/node-groups.json
```

```json
{
  "Groups": [
    {
      "Name": "Production Cloud",
      "NodeIds": ["la.realiteam.art", "oc1.realiteam.art"]
    }
  ]
}
```

---

## Host Deep Dive & Metrics Tab

Selecting any node in the fleet view opens the Host Detail view immediately, shifting views without waiting for network or query completion.

### Instant View Transition & Graph Loading State

- **Immediate View Switch**: Selecting a node instantly updates the current view and sidebar selection, ensuring zero UI lag or freezing on the fleet overview.
- **Visual Loading Indicators**: When opening a host or switching time scopes, metric charts render a prominent `"Loading measurements…"` status in accent cyan (`#06B6D4`) in the center of the plot canvas, while graph card headers display `"Loading…"`.
- **Buffered Live Telemetry**: Incoming 1Hz live telemetry updates are buffered cleanly without overwriting the loading indicator, keeping the status stable until the historical baseline query finishes and is applied to the graph.

### Pinned Slim Control Bar

The Host Detail view features a compact slim bar:
- Displays node ID, telemetry mode badge, and CPU model summary.
- Houses the quick target node switcher dropdown and the **⚙ Graph Settings** gear.

### Telemetry Graph Scopes

The pinned scope bar provides rapid time-window switching:

| Scope | Range | Ingest / Resolution Target |
|---|---|---|
| **1m** | Last 60 seconds | Raw 1Hz live rolling ring buffer (microsecond zero-RPC access) |
| **5m** | Last 5 minutes | Raw 1Hz live buffer / collector query |
| **30m** | Last 30 minutes | Downsampled LTTB TSDB query (~1,200 points) |
| **2h** | Last 2 hours | Downsampled LTTB TSDB query (~1,200 points) |
| **6h** | Last 6 hours | Downsampled LTTB TSDB query (~1,200 points) |
| **12h** | Last 12 hours | Downsampled LTTB TSDB query (~1,200 points) |
| **24h** | Last 24 hours | Downsampled LTTB TSDB query (~1,200 points) |
| **Custom** | Arbitrary start/end | Custom calendar picker with second precision |

### Custom Scope (Date & Time Picker)

Clicking the calendar icon (**📅**) opens the custom time window modal:
- Pick arbitrary starting and ending dates and times.
- Built-in validation prevents inverted time selections (start time after end time).

### Resolution-Adaptive Downsampling

MADTOM utilizes the **Largest-Triangle-Three-Buckets (LTTB)** algorithm to preserve critical peaks, spikes, and valleys while downsampling millions of raw data points to the exact pixel budget of the display graph (defaulting to 1,200 points).

### Graph Navigation: Zoom, Pan & Page Scrolling

- **Zoom**: Scroll the mouse wheel while hovering over a chart canvas to zoom in/out anchored around the cursor. The zoom ceiling is dynamic, scaling with the operator's configured history retention ratio (`history_retention_ratio * 10`, reaching up to 100× magnification when retention is set to 10×) to allow fine-grained sub-second inspection.
- **Pan**: Click and drag horizontally to pan backward and forward across historical time windows.
- **Page Scrolling**: Mouse wheel scrolling outside chart canvases smoothly scrolls the page vertically.

### Custom Graph Layouts & Presets

- **Custom Colors**: Click any series pill to open the HSV/RGB color wheel to personalize line colors.
- **Graph Groups & Rows**: Reorder cards, place graphs side-by-side, or split multi-metric graphs.
- **Presets**: Save and restore complete graph dashboards using the Presets dropdown.
- **Persistence Files**:
  - `~/.local/share/MADTOM/graphs.json` (stores custom layout per node)
  - `~/.local/share/MADTOM/graph-presets.json` (stores saved named presets)

### Multi-Device Disk I/O Metrics

MADTOM automatically detects all active storage devices:
- Individual and aggregated read/write throughput (MB/s).
- IOPS rates and latency diagnostics.
- Supports virtual block devices (`nvme*`, `sd*`, `vd*`, `xvd*`, `mapper/*`).

### Aggregated Mode & Time-Based Bucketing Pool

When viewing aggregated cluster metrics or monitoring top-right pinned metrics:
- **Time-Based Bucketing**: The `ClusterAggregationService` gathers telemetry arrivals across nodes into synchronized 1-second time buckets. A bucket flushes as soon as all active cluster nodes report or when the bucket timeout fires, eliminating erratic UI flickering.
- **Selective Cluster-Wide Merging**: Only cluster-wide relevant metrics are merged (total CPU load, total memory, aggregated network transit, disk throughput, and TWAMP latency). Individual per-thread/per-core metrics are strictly excluded from the aggregated pool to prevent improper averaging.

### Per-Core & Per-Thread Graphing

MADTOM exposes individual CPU core/thread metrics in Deep Dive graphs:
- **Single-Node Mode**: Cores appear as `Thread {c}` (e.g. `Thread 0`, `Thread 1`) with direct history lookup and live updates scaled to 0–100%.
- **Aggregated Mode**: Cores across all cluster hosts are uniquely identified as `{nodeId} - Thread {c}` (e.g. `node1 - Thread 0`). Each node's individual threads can be plotted on graphs simultaneously without being merged into a single averaged line.

### Cumulative Counter Rate Derivation

Cumulative counter metrics (`nic.*.rx_bytes`, `nic.*.tx_bytes`, `disk.io.*.read_bytes`, `disk.io.*.write_bytes`, `disk.io.*.read_ops`, `disk.io.*.write_ops`) automatically default to rate-of-change mode when added to charts. History queries differentiate rates on a per-node basis before bucket aggregation, eliminating tiny `dt` division anomalies and spurious multi-gigabit rate spikes.

---

## Collector & Fleet Management

### Adding, Editing, and Removing Collectors

1. Click **Collectors** in the main sidebar.
2. Enter the collector gRPC address (e.g. `collector.example.com:50051`).
3. Set connection timeouts and reverse-push tokens if applicable.

### Collector Persistence (`collectors.json`)

Configured collector endpoints persist to:
```bash
~/.local/share/MADTOM/collectors.json
```

```json
{
  "Collectors": [
    {
      "Name": "Primary Hub",
      "Endpoint": "127.0.0.1:50051",
      "Enabled": true
    }
  ]
}
```

### Opt-In Global Metrics & Top-Bar Pinning

Operators can pin cluster-wide aggregate metrics (e.g. total fleet CPU, collective network ingress/egress) to the top bar:
- Configured in the Collector Settings panel.
- Saved to `~/.local/share/MADTOM/global-metrics.json`.

### Node Opt-In Settings & Safe Apply Workflow

When changing telemetry collection settings on remote daemons (e.g., process monitoring tier, per-core CPU, disk devices):
- Changes are staged in the UI.
- The **Apply Changes** button triggers a safe RPC push to the collector and target daemon, confirming successful application before persisting to `~/.local/share/MADTOM/optin-settings.json`.

---

## Process Monitoring & Storage Modes

### Three-Tier Collection Policy

Remote endpoints support four configurable process telemetry collection modes:

| Mode | Overhead | Behavior |
|---|---|---|
| **Off** | Zero | Process scrapers disabled completely on the node. |
| **Inactive** | Negligible | Daemon scrapes processes only when an operator actively opens the Processes tab. |
| **Live** | Low (~1% CPU) | Continuous 1Hz process scraping streamed to the collector for live viewing. |
| **Full (TSDB)** | Moderate | Continuous scraping with historical storage in Pebble TSDB for time-series breakdown analysis. |

### Top-N Process Count Configuration

Configure how many top CPU/memory consuming processes the endpoint tracks (from Top 5 up to Top 50) using the slider in Node Settings.

### Processes Manager: Sub-Tabs & Breakdown Charting

- **True Top N Overview**: View the active process hierarchy sorted by CPU%, RSS Memory, or Disk Write Rate.
- **Process Breakdown Chart**: Stacked multi-series charts showing individual process consumption over the selected historical time scope.
- **Lazy Query Scheduling**: To preserve collector gRPC query slots for active metric charts, historical process queries are only scheduled when the Process Overview tab is actively visible.

---

## TWAMP Flight & Latency Radar

MADTOM features RFC 5357 **Two-Way Active Measurement Protocol (TWAMP Light)**:
- Prober measures bidirectional microsecond-accurate network latency.
- **Radar Visualizer**: Polar chart showing forward vs. reverse packet travel times.
- **Asymmetric Routing Detection**: Visualizes asymmetric route delays and network congestion anomalies in real time.

---

## Diagnostics & Performance Tuning

### Background Ingestion Worker & Coalesced UI Drains

To maintain smooth 60 FPS presentation even during multi-node bursts or high-frequency 10Hz streaming:
- **Offloaded Ingestion**: Rate calculations, disk throughput deltas, process ranking, and `HistoryCache.Record()` execute on dedicated background worker threads.
- **Coalesced Drains**: UI updates are staged in an atomic latest-snapshot slot and scheduled via `Interlocked.CompareExchange` onto the Avalonia UI dispatcher, preventing event queue backlog.

### Zero-Allocation Sparklines & Deferred Pruning

- **In-Place Sparkline Buffers**: Live 60-second node card sparklines shift points in-place using fixed-size arrays and memory copy, eliminating transient heap allocations on every sample.
- **Deferred Cache Pruning**: Queue traversal and sample pruning are deferred to block-seal boundaries and periodic timer maintenance, keeping steady-state sample recording lightweight.

### Client History Cache & Off-Lock Decompression

To ensure zero UI thread stalls even when querying millions of historical records:
- Telemetry points are stored in compressed sealed Zstandard blocks.
- Block decompression and downsampling occur asynchronously on background thread pools outside the cache lock.
- Cached point queries execute in microsecond time budgets.

### Compression Diagnostics Window

Open the **Compression Diagnostics** panel to inspect:
- Total cache memory occupancy (live vs. stored tiers).
- Zstandard compression ratios (typically 4:1 to 8:1).
- Sealed block counts and active memory savings.

### History Timing Diagnostics

When debugging historical query latency, set:
```bash
export MADTOM_UI_TIMING=1
MADTOM.Console 2> ui-performance.log
```
The timing subsystem logs lock wait/hold durations, gRPC network fetch times, downsample runtimes, and dispatcher frame latency.

### Deep-Dive Transition Timing Diagnostics

To measure the end-to-end latency from the moment an operator clicks a node card until all historical charts in the deep-dive view are fully populated and rendered:
```bash
export MADTOM_DEEPDIVE_TIMING=1
# Or enable the full UI performance diagnostics suite:
export MADTOM_UI_TIMING=1
MADTOM.Studio 2> deepdive-transition.log
```

The transition tracker measures each step in the pipeline:
- **`navigation.click`**: Moment the node card, sidebar host, or dropdown item is clicked.
- **`navigation.view-switch`**: Immediate view model swap (`CurrentView = HostDetailView`).
- **`host-detail.specs`**: Header properties and hardware specs update (CPU model, RAM%, TWAMP).
- **`metrics.save-layout`**: Serializing previous host's layout to `graphs.json` if changes were pending.
- **`metrics.load-layout`**: Loading target host's layout from `graphs.json` and instantiating graph view models.
- **`metrics.populate-metrics`**: Scanning and populating available metric series.
- **`data.query-wall`**: Asynchronous gRPC queries to collector TSDB and cache lookups.
- **`data.transform`**: Background rate calculations, point sorting, LTTB downsampling, and time labels.
- **`data.batch-publish`**: UI dispatcher batch update (`ApplySnapshots`) clearing loading flags.
- **`chart.render-all`**: Render completion across all visible chart canvases (`MetricHistoryChartControl.Render`).

### UI Performance Baselining

Analyze captured logs with the performance summarizer tool:
```bash
python3 tools/Telemetry/summarize_ui_performance.py ui-performance.log
python3 tools/summarize_ui_performance.py deepdive-transition.log
```

The tool produces a breakdown of zero-RPC vs. remote queries, cache hit ratios, and 95th/99th percentile frame rendering latencies.
Example transition summary:
```text
Deep-dive transition summaries (moment node clicked -> all charts rendered):
la.realiteam.art (from fleet) | completed | n=3 median=124.5ms p95=142.1ms charts=4/4 | viewSwitchAvg=1.20ms specsAvg=2.10ms saveLayoutAvg=0.80ms loadLayoutAvg=3.40ms populateAvg=1.10ms queryWallAvg=98.40ms transformAvg=8.20ms publishAvg=1.80ms renderChartsAvg=7.50ms
```

The tool also produces a breakdown of zero-RPC vs. remote queries, cache hit ratios, and 95th/99th percentile frame rendering latencies.

---

## System Journal Log Streaming & Virtual Viewer

The **Logs Tab** within the Host Deep Dive provides end-to-end streaming of `systemd-journald` log entries over gRPC with high-efficiency virtualization and client-side LRU chunk caching.

### Three-Tier Opt-In Policy

Log capture is completely opt-in and disabled by default per node:
1. **`OPT_IN_OFF` (Default)**: The daemon does not run `journalctl`, attaches zero log chunks to batches, and uses zero network bandwidth and zero disk writes.
2. **`OPT_IN_MONITOR_ONLY`**: The daemon tails `journalctl -o json -f`, batches entries into compressed chunks (256 records/chunk), and streams them live to connected UI clients. Zero log chunks are written to the daemon disk WAL or collector Pebble database.
3. **`OPT_IN_MONITOR_AND_STORE`**: Chunks are streamed live to connected clients and persisted in the collector's Pebble TSDB log store with retention policies (hours and MiB quota) and offline WAL spooling on the daemon.

### Monospace Virtual Log Viewer (`VirtualLogViewControl`)

To handle tens of thousands of lines of log entries without UI lag or memory blowup:
- **Mathematical Line Projection**: Uses fixed line height (`18px`) and monospace typography. Scrollbar ranges and thumb dimensions map directly to monotonic sequence numbers (`FirstSeq` to `LastSeq`).
- **Off-Screen Compression**: Only records inside the current viewport (+ overscan) are decompressed from zstd bytes. Offscreen chunks remain compressed in the client LRU cache.
- **Chunk Prefetching**: When scrolling into unbuffered history ranges, the control emits prefetch requests via `NeedChunk` to retrieve missing chunks seamlessly from the collector without freezing UI frames.
- **Follow Mode & Floating Jump Chip**: Follow mode auto-scrolls to the newest log entries as they arrive. When the user scrolls up to review history, follow mode pauses; if new entries arrive, a floating **"Jump to latest (↓)"** chip appears to return to live head with one tap.

### Client Cache & Performance Settings

Under **Collector Settings → Performance**, operators can configure:
- **Log Compressed Cache Quota**: Memory limit (default `128 MiB`, 16–4096 MiB) for raw zstd-compressed chunks across all nodes.
- **Decoded Chunks Pool**: Limit (default `32 chunks`, 4–256 chunks) of decompressed chunk lists retained in hot memory for viewport rendering.
- **Clear Log Cache**: One-click purge of all local client log chunks without interrupting running collector streams.

