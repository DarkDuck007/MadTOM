# MADTOM Telemetry: UI & Visualization Guide

This guide covers the **MADTOM Telemetry Plugin** (`MADTOM.Plugins.Telemetry`) and its standalone application (`MADTOM.Plugins.Telemetry.App`). It explains the fleet dashboard, real-time and historical telemetry charts, time scopes, downsampling, process monitoring, TWAMP latency radar, diagnostic panels, and persistent configuration stores.

---

## Table of Contents

- [Overview & Standalone Mode](#overview--standalone-mode)
- [Fleet Dashboard](#fleet-dashboard)
  - [Live 1Hz Sparklines & Health Badges](#live-1hz-sparklines--health-badges)
  - [Dynamic Node Grouping & Fleet Filtering](#dynamic-node-grouping--fleet-filtering)
  - [Node Group Persistence (`node-groups.json`)](#node-group-persistence-node-groupsjson)
- [Host Deep Dive & Metrics Tab](#host-deep-dive--metrics-tab)
  - [Pinned Slim Control Bar](#pinned-slim-control-bar)
  - [Telemetry Graph Scopes](#telemetry-graph-scopes)
  - [Custom Scope (Date & Time Picker)](#custom-scope-date--time-picker)
  - [Resolution-Adaptive Downsampling](#resolution-adaptive-downsampling)
  - [Graph Navigation: Zoom, Pan & Page Scrolling](#graph-navigation-zoom-pan--page-scrolling)
  - [Custom Graph Layouts & Presets](#custom-graph-layouts--presets)
  - [Multi-Device Disk I/O Metrics](#multi-device-disk-io-metrics)
- [Collector & Fleet Management](#collector--fleet-management)
  - [Adding, Editing, and Removing Collectors](#adding-editing-and-removing-collectors)
  - [Collector Persistence (`collectors.json`)](#collector-persistence-collectorsjson)
  - [Opt-In Global Metrics & Top-Bar Pinning](#opt-in-global-metrics--top-bar-pinning)
  - [Node Opt-In Settings & Safe Apply Workflow](#node-opt-in-settings--safe-apply-workflow)
- [Process Monitoring & Storage Modes](#process-monitoring--storage-modes)
  - [Three-Tier Collection Policy](#three-tier-collection-policy)
  - [Top-N Process Count Configuration](#top-n-process-count-configuration)
  - [Processes Manager: Sub-Tabs & Breakdown Charting](#processes-manager-sub-tabs--breakdown-charting)
- [TWAMP Flight & Latency Radar](#twamp-flight--latency-radar)
- [Diagnostics & Performance Tuning](#diagnostics--performance-tuning)
  - [Client History Cache & Off-Lock Decompression](#client-history-cache--off-lock-decompression)
  - [Compression Diagnostics Window](#compression-diagnostics-window)
  - [History Timing Diagnostics](#history-timing-diagnostics)
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

Selecting any node in the fleet view opens the Host Detail view.

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

- **Zoom**: Scroll the mouse wheel while hovering over a chart canvas to zoom in/out anchored around the cursor.
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

---

## TWAMP Flight & Latency Radar

MADTOM features RFC 5357 **Two-Way Active Measurement Protocol (TWAMP Light)**:
- Prober measures bidirectional microsecond-accurate network latency.
- **Radar Visualizer**: Polar chart showing forward vs. reverse packet travel times.
- **Asymmetric Routing Detection**: Visualizes asymmetric route delays and network congestion anomalies in real time.

---

## Diagnostics & Performance Tuning

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

### UI Performance Baselining

Analyze captured logs with the performance summarizer tool:
```bash
python3 tools/Telemetry/summarize_ui_performance.py ui-performance.log
```

The tool produces a breakdown of zero-RPC vs. remote queries, cache hit ratios, and 95th/99th percentile frame rendering latencies.

