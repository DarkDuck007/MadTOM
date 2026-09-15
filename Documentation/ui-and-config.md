# UI Guide & Configuration Reference

This guide covers the MADTOM Desktop Operator UI, its features, telemetry visualization capabilities, and persistent configuration stores.

---

## Table of Contents

1. [Desktop UI Overview](#desktop-ui-overview)
   - [UI Scaling & Display Multiplier](#ui-scaling--display-multiplier)
   - [Interactive Resizable Sidebars](#interactive-resizable-sidebars)
   - [Symmetrically Resizable Centered Modals](#symmetrically-resizable-centered-modals)
   - [Modal Escape Key Navigation](#modal-escape-key-navigation)
2. [Configuration & Persistent Storage](#configuration--persistent-storage)
   - [Location & Resolving Paths](#location--resolving-paths)
   - [`graphs.json` — Custom Graph Groups & Layouts](#graphsjson--custom-graph-groups--layouts)
   - [`graph-presets.json` — Layout Presets](#graph-presetsjson--layout-presets)
   - [`node-groups.json` — Custom Node Grouping](#node-groupsjson--custom-node-grouping)
   - [`global-metrics.json` — Pinned Top-Bar Metrics & Modifiers](#global-metricsjson--pinned-top-bar-metrics--modifiers)
   - [`collectors.json` — Saved Collector Hubs](#collectorsjson--saved-collector-hubs)
   - [`settings.json` — Persistent User Preferences](#settingsjson--persistent-user-preferences)
   - [`themes/` — Custom Runtime Themes (YAML / JSON)](#themes--custom-runtime-themes-yaml--json)
3. [Telemetry Graph Scopes & Resolution](#telemetry-graph-scopes--resolution)
   - [Pinned Slim Scope Bar](#pinned-slim-scope-bar)
   - [Relative Scopes (1m, 5m, 30m, 2h, 6h, 12h, 24h)](#relative-scopes-1m-5m-30m-2h-6h-12h-24h)
   - [Custom Scope (Date & Time Picker)](#custom-scope-date--time-picker)
   - [Resolution-Adaptive Downsampling (LTTB)](#resolution-adaptive-downsampling-lttb)
   - [Graph Performance Settings](#graph-performance-settings)
   - [Graph Navigation: Zoom, Pan & Page Scrolling](#graph-navigation-zoom-pan--page-scrolling)
   - [Memory Normalization in Tooltips](#memory-normalization-in-tooltips)
   - [Graph Groups, Side-by-Side Rows & Reordering](#graph-groups-side-by-side-rows--reordering)
   - [Custom Color Wheel & Series ColorPicker](#custom-color-wheel--series-colorpicker)
   - [Missing Data & Downtime Handling](#missing-data--downtime-handling)
   - [Multi-Device Disk I/O Metrics & Diagnostics](#multi-device-disk-io-metrics--diagnostics)
4. [Collector & Fleet Management](#collector--fleet-management)
   - [Client History Cache](#client-history-cache)
   - [Opt-In Global Metrics & Top Bar Pinning](#opt-in-global-metrics--top-bar-pinning)
   - [Dynamic Node Grouping & Fleet Filtering](#dynamic-node-grouping--fleet-filtering)
   - [Node Opt-In Settings & Safe Apply Workflow](#node-opt-in-settings--safe-apply-workflow)
   - [Adding a Collector](#adding-a-collector)
   - [Editing a Collector](#editing-a-collector)
   - [Removing a Collector](#removing-a-collector)
   - [Multi-Hub Aggregation](#multi-hub-aggregation)
5. [Process Monitoring, Storage Modes & Graphing](#process-monitoring-storage-modes--graphing)
   - [Three-Tier Collection Policy](#three-tier-collection-policy)
   - [Top-N Process Count Configuration](#top-n-process-count-configuration)
   - [Historical TSDB Metric Storage & Breakdown Charting](#historical-tsdb-metric-storage--breakdown-charting)
   - [Processes Manager: Sub-Tabs & True Top N Overview](#processes-manager-sub-tabs--true-top-n-overview)
6. [Theming & Display Contrast Profiles](#theming--display-contrast-profiles)
   - [Accessing Theme & Lexicon Settings](#accessing-theme--lexicon-settings)
   - [Built-in Palettes](#built-in-palettes)
   - [Dynamic Theme Loading & File Watching](#dynamic-theme-loading--file-watching)
   - [Plugin Theme Overrides](#plugin-theme-overrides)
   - [Custom Theme Schema & Example](#custom-theme-schema--example)

---

## Desktop UI Overview

MADTOM provides a rich, responsive cross-platform desktop interface written in **C# / .NET 10** using **Avalonia UI**.

- **Executable**: `MADTOM.Console` (packaged in `MADTOM_DOTNET/publish/{OS_arch}/MADTOM.Console/MADTOM.Console`)
- **Key Tabs & Views**:
  - **Fleet Dashboard**: Grid and list view of all monitored nodes across all connected collectors with 1Hz live sparklines and health badges.
  - **Host Detail**:
    - **Top-Bar Navigation & Slim Control Bar**: The global top bar hosts the back button (`← Nodes`) and active node title with a floating CPU specification tooltip on mouse hover. Inside the node deep dive, a compact slim bar displays the telemetry type badge, graph settings gear (`⚙`), CPU model, and a 20% narrower target node selector, removing redundant hardware boxes to maximize vertical space for charts.
    - **Metrics Tab**: Real-time canvas graphs with zoom, pan, hover tooltips, and customizable multi-metric series.
    - **Processes Tab**: Live process tree, thread counts, and memory/CPU sorting.
    - **TWAMP Flight Tab**: Asymmetry radar and round-trip flight times.
    - **Logs Tab**: Node-level activity logs.

### UI Scaling & Display Multiplier

MADTOM Console features an app-wide layout scaling engine powered by Avalonia's `LayoutTransformControl` and `ScaleTransform`:
- **Dynamic Multiplier**: Scales all interface elements, navigation rails, typography, flyouts, and canvas charts from **10% (0.1×) up to 1000% (10.0×)** with pixel-accurate layout recalculation.
- **Console Settings Flyout**: Accessible via the gear icon (**⚙**) on the top-left header bar.
  - **Live Scale Readout**: Displays current percentage (e.g. `100% (1.0x)`).
  - **Smooth Slider**: Continuous adjustment from 10% to 1000% with tick markers.
  - **Quick Preset Chips**: Fast one-click jumps to `50%`, `75%`, `100%`, `150%`, and `200%`.
  - **Reset Button**: One-click restoration back to default 100% scale.
- **Persistence**: Saved to `UiScalePercent` in `~/.local/share/MADTOM/settings.json` and restored on startup.

### Interactive Resizable Sidebars

Both the top-level Console navigation rail and the Telemetry plugin node sidebar can be interactively resized:
- **Draggable Splitters**: Transparent 4px splitters sit between the navigation rails and main viewports. Dragging horizontally resizes the sidebar in real time.
- **Width Bounds**: Uncollapsed width is clamped between 140px and 600px to maintain readability.
- **Collapsible Toggle**: Clicking the sidebar toggle button cleanly collapses the rail down to 64px icon-only mode while preserving the custom uncollapsed width for when it is reopened.

### Symmetrically Resizable Centered Modals

Dialog overlays—including **Customize Graphs & Telemetry** and **Collector Endpoints & Node Settings**—feature mouse-drag resizing with symmetrical expansion:
- **8 Edge & Corner Hit Zones**: Operators can click and drag any of the 4 borders (`Left`, `Right`, `Top`, `Bottom`) or 4 corners (`TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`).
- **Symmetric Centering**: Dragging any edge or corner symmetrically expands or contracts the dialog around its center anchor, keeping the modal centered on screen.
- **Minimum Enforced Bounds**: Modals cannot be resized below functional thresholds (minimum 520px width and 400px height).

### Modal Escape Key Navigation

All modal dialogs support keyboard dismissal:
- Pressing the **`Esc`** key when `CustomizeGraphsModal`, `CollectorSettingsModal`, `CustomScopeModal`, or `ActionConfirmModal` is visible triggers the modal's close/back action.
- Uses tunneling input events (`RoutingStrategies.Tunnel`), ensuring `Esc` is captured even if a child input or button within the modal currently holds keyboard focus.

---

## Configuration & Persistent Storage

All operator configuration files are stored locally in the standard OS application data directory.

### Location & Resolving Paths

Resolved via .NET `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)`:

| Operating System | Path |
|---|---|
| **Linux** | `~/.local/share/MADTOM/` |
| **Windows** | `%LOCALAPPDATA%\MADTOM\` (`C:\Users\<User>\AppData\Local\MADTOM\`) |
| **macOS** | `~/Library/Application Support/MADTOM/` |

---

### `graphs.json` — Custom Graph Groups & Layouts

Stores the customized dashboard graph layout per node and cluster view. Layouts are organized into **Graph Groups** (rows): each group displays any number $N$ of graphs horizontally side-by-side. Each individual graph can display one or more multi-metric aggregated series with custom titles and line colors.

**Example `graphs.json`**:
```json
{
  "aggregated": [
    {
      "Title": "Row 1: System",
      "Graphs": [
        {
          "Title": "CPU Breakdown",
          "Series": [
            { "Metric": "cpu.total", "Label": "Total", "ColorHex": "#06B6D4" },
            { "Metric": "cpu.user", "Label": "User", "ColorHex": "#3B82F6" },
            { "Metric": "cpu.system", "Label": "System", "ColorHex": "#8B5CF6" },
            { "Metric": "cpu.iowait", "Label": "IOWait", "ColorHex": "#F59E0B" }
          ]
        },
        {
          "Title": "Memory Breakdown",
          "Series": [
            { "Metric": "memory.used", "Label": "Used", "ColorHex": "#10B981" },
            { "Metric": "memory.available", "Label": "Available", "ColorHex": "#34D399" }
          ]
        }
      ]
    }
  ],
  "host-worker-01": [
    {
      "Title": "Network Throughput",
      "Graphs": [
        {
          "Title": "eth0 Ingress & Egress",
          "Series": [
            { "Metric": "nic.eth0.rx_bytes", "Label": "RX", "ColorHex": "#10B981", "IsRateOfChange": true },
            { "Metric": "nic.eth0.tx_bytes", "Label": "TX", "ColorHex": "#3B82F6", "IsRateOfChange": true }
          ]
        }
      ]
    }
  ]
}
```

- **Per-Node & Aggregated Scoping**: Layout configurations are separated by node ID (`host-worker-01`, etc.) as well as the global cluster `"aggregated"` mode.
- **Backward Compatibility**: MADTOM automatically migrates older flat `List<GraphItemConfig>` and legacy `string[]` formats into grouped rows seamlessly upon load.
- Changes are written atomically (`graphs.json.tmp` -> `graphs.json`) upon user interaction.

---

### `graph-presets.json` — Layout Presets

Stores reusable layout presets that can be applied across different nodes or aggregated cluster views.

**Example `graph-presets.json`**:
```json
[
  {
    "Id": "a1b2c3d4e5f6",
    "Name": "Network & Latency Ops",
    "Description": "High-density network view with TWAMP latency and interface throughput",
    "IsBuiltIn": false,
    "Groups": [
      {
        "Title": "Latency",
        "Graphs": [
          {
            "Title": "TWAMP Latency Breakdown",
            "Series": [
              { "Metric": "twamp.rtt", "Label": "RTT", "ColorHex": "#06B6D4" },
              { "Metric": "twamp.forward", "Label": "Forward", "ColorHex": "#6366F1" },
              { "Metric": "twamp.reverse", "Label": "Reverse", "ColorHex": "#A855F7" }
            ]
          }
        ]
      }
    ]
  }
]
```

- **Built-In Presets**: MADTOM ships with 4 standard built-in presets:
  1. **Standard Stack**: Full-width vertically stacked CPU Breakdown and Memory Breakdown.
  2. **Dual Side-by-Side**: Compact 2-column layout with CPU & Memory on Row 1, Network & TWAMP on Row 2.
  3. **Quad Horizontal Grid**: High-density 4 graphs side-by-side in a single row.
  4. **Network & Latency Focus**: Full-width TWAMP breakdown with side-by-side interface RX/TX rates.
- **Accessing Presets in Settings**: To prevent clutter on the live metrics dashboard, layout presets are managed inside the **⚙ Customize Graphs & Telemetry** modal rather than in a persistent top toolbar.
- **Custom Presets**: Operators can click **Save As Preset…** directly within the customization modal to save the current grouped layout, assign a custom name and description, and apply it to any node. Built-in presets are protected, while custom user presets can be deleted safely at any time.

---

### `node-groups.json` — Custom Node Grouping

Stores custom user-defined operational groups (e.g., `Compute`, `Storage`, `Edge`, `Database`, `US-West`) mapped to individual node IDs. This completely replaces legacy, hardcoded "Dedicated Host / VM" filters with flexible, user-driven fleet organization.

**Example `node-groups.json`**:
```json
{
  "node-worker-01": "Compute",
  "node-worker-02": "Compute",
  "storage-cluster-01": "Storage",
  "edge-gw-frankfurt": "Edge"
}
```

- **Unassigned Nodes ("None")**: Nodes not present in `node-groups.json`, or assigned `"None"` / empty string, have no custom group tag (`""`). They are displayed under the **All** fleet filter without generating redundant or confusing filter pills.
- **Strict Decoupling from 1Hz Telemetry Ticks**: Node groups are strictly decoupled from the live 1Hz telemetry streaming loop. The telemetry provider streams hardware and OS metrics without touching or re-evaluating node groups. Live metric arrivals never overwrite group assignments, resurrect deleted groups, or clobber input fields.
- **Fleet Card Badges**: Each host card displays its assigned group badge alongside hardware and network health indicators.
- **Dynamic Fleet Filter Pills**: The fleet dashboard automatically aggregates all active non-empty group names across configured nodes and presents interactive filter pills (`All`, `Compute`, `Storage`, etc.). Clicking a pill instantly filters the fleet view.
- **Node Groups Configuration Tab**: Operators can assign or change node groups in the **Node Groups** tab of the Collector Settings dialog (accessible via the ⚙ button in the fleet toolbar), with one-click quick assignment buttons (`Compute`, `Storage`, `Edge`, `None`) or free-form custom names.

---

### `global-metrics.json` — Pinned Top-Bar Metrics & Modifiers

Stores the operator's customized opt-in selection of fleet-wide aggregated metrics pinned to the top-right application header bar.

**Example `global-metrics.json`**:
```json
[
  {
    "MetricKey": "network.ingress",
    "Modifier": "Sum",
    "IsPinned": true,
    "Order": 0
  },
  {
    "MetricKey": "network.egress",
    "Modifier": "Sum",
    "IsPinned": true,
    "Order": 1
  },
  {
    "MetricKey": "cpu.total",
    "Modifier": "Avg",
    "IsPinned": true,
    "Order": 2
  }
]
```

- **Supported Metrics**: `network.ingress`, `network.egress`, `cpu.total`, `memory.used`, `disk.io.read_bytes`, `disk.io.write_bytes`, `disk.io.ops`, `twamp.rtt`.
- **Dynamic Per-Device & Per-Interface Metrics**: MADTOM automatically discovers all detected block devices (`sda`, `sda1`, `sdb`, `nvme0n1`, etc.) and network interfaces (`eth0`, `wg0`, etc.) across connected hosts. Operators can select specific devices (e.g. `disk.io.sda.read_bytes`, `disk.io.sda.write_bytes`, `disk.io.sda.read_ops`, `disk.io.sda.write_ops`, `nic.eth0.rx_bytes`, `nic.eth0.tx_bytes`) to pin to the top-right bar.
- **Supported Aggregation Modifiers**:
  - `Sum`: Aggregates the sum of values across all monitored nodes.
  - `Avg`: Computes the arithmetic mean across all active nodes.
  - `Rate of Change (/s)`: Calculates the live second-by-second derivative ($\Delta / \text{sec}$) of the fleet total.
- **Default Top-Bar Metrics**: When `global-metrics.json` does not exist, MADTOM pins `network.ingress` (`Sum`) and `network.egress` (`Sum`).
- **Interactive Top-Bar Pills**: Each pinned metric renders as a compact, clickable chip in the top-right header featuring an icon, short label, live value, modifier badge (`SUM`, `AVG`, `Δ/s`), and quick settings shortcut.

---

### `collectors.json` — Saved Collector Hubs

Stores the list of collector endpoints configured in the UI.

**Example `collectors.json`**:
```json
[
  {
    "Name": "Localhost",
    "Address": "127.0.0.1:50051"
  },
  {
    "Name": "Production Hub",
    "Address": "collector.company.internal:50051"
  }
]
```

- If `collectors.json` does not exist on first launch, it defaults to `Localhost` (`127.0.0.1:50051`).
- Adding or removing collectors in the **Collector Settings** dialog immediately updates and persists to this file.

---

### `settings.json` — Persistent User Preferences

Stores runtime operator interface preferences across sessions.

**Example `settings.json`**:
```json
{
  "Theme": "default-dark",
  "Language": "goose",
  "IsConsoleSidebarCollapsed": false,
  "IsTelemetrySidebarCollapsed": false,
  "UiScalePercent": 100
}
```

- **`Theme`**: Active theme profile key (e.g. `default-dark`, `pure-light`, `paper-white`, `minimal-mono`, `anti-bleed-grey`, `tft-amber-terminal`, `high-contrast`, `solarized-dark`).
- **`Language`**: Active lexicon terminology dialect (`goose`, `feline`, or `standard`).
- **`IsConsoleSidebarCollapsed`**: Navigation rail collapse state for the `MADTOM.Console` host.
- **`IsTelemetrySidebarCollapsed`**: Navigation rail collapse state for the Telemetry plugin module.
- **`UiScalePercent`**: Application-wide UI display scaling percentage (10 to 1000, default 100).
- All state changes are written atomically (`settings.json.tmp` -> `settings.json`) upon user interaction and restored seamlessly on startup.

---

### `themes/` — Custom Runtime Themes (YAML / JSON)

Stores user-created custom themes discovered and loaded dynamically at runtime.

- **Directory Path**: `~/.local/share/MADTOM/themes/` (Linux), `%LOCALAPPDATA%\MADTOM\themes\` (Windows), `~/Library/Application Support/MADTOM/themes/` (macOS).
- **Supported Formats**: `.yaml`, `.yml`, `.json`.
- **Live File Watching**: MADTOM mounts a `FileSystemWatcher` on this directory. When files are added, modified, or removed, the UI theme picker dynamically updates its available options without requiring application restarts or code rebuilds.
- **Auto-Discovery**: Themes dropped into this folder are automatically placed into the `"Custom"` category unless a specific `category` field is defined in the file.

---

## Telemetry Graph Scopes & Resolution

### Pinned Slim Scope Bar

The time scope toolbar is pinned directly at the top of the **Metrics Tab** viewport (immediately beneath the host tab bar):
- **Fixed Anchor**: Positioned in `Row 0` outside the dashboard `ScrollViewer`, ensuring time-window controls remain accessible at all times while scrolling through dozens of graphs.
- **Slim Modern Aesthetic**: Designed with edge-to-edge square borders (`CornerRadius="0"`), a subtle bottom border (`BorderThickness="0,0,0,1"`), and compact vertical padding (`Padding="8,4"`).
- **Streamlined Controls**: Redundant "Historical telemetry" text labels, status dots, and duplicate customize buttons have been removed from the scope toolbar. Customization is cleanly anchored to the primary host header.

### Relative Scopes (1m, 5m, 30m, 2h, 6h, 12h, 24h)
Relative scopes query a moving window anchored to current time:
`[DateTime.UtcNow - scope, DateTime.UtcNow]`

- **1m Scope**: 1Hz high-frequency view with ~60 points.
- **5m Scope**: Intermediate view refreshed every 2 seconds.
- **30m / 2h Scopes**: Medium trend views refreshed every 5 seconds.
- **6h / 12h / 24h Scopes**: Broad trend and diurnal shift views refreshed every 5 seconds.

> [!NOTE]
> If a node is offline or replaying backlogged WAL data, metrics in a relative window will end at the node's latest timestamp, and the graph status displays: `(data ends at HH:mm:ss)`.

### Custom Scope (Date & Time Picker)
Clicking the **Custom** scope button opens a modal to select precise start and end dates and times:
- Custom scopes interpret dates and times in the **operator's local machine timezone** (e.g. JST, UTC+9) and convert them to UTC for the TSDB query.
- Custom windows remain frozen at the selected bounds and do not refresh automatically with incoming live samples.

### Resolution-Adaptive Downsampling (LTTB)
Metric graphs request a point budget of **three times the plotting width by default** (in logical pixels), leaving extra detail for zooming. A 750-pixel plotting area requests up to 2,250 points; a 30-minute history sampled once per second therefore fits without reduction. Before layout, the budget defaults to 2,400 points. Resizing refreshes history after a 200 ms debounce, and cached collector responses are reused only when their resolution is sufficient.

The collector applies **Largest-Triangle-Three-Buckets (LTTB)** to the requested budget. After merging collector and local history and calculating counter rates, the client reduces dense series using time buckets that retain first, minimum, maximum, and last values. Cached and newly streamed samples use the same display budget. Client history and drawing buckets use fixed absolute-time boundaries for a given scope and resolution. Scrolling therefore keeps completed interior buckets stable, with original sample timestamps and values preserved. The newest incomplete bucket and the window edges can still change as samples arrive or expire; resizing, zooming, changing density, collector refetches, and automatic Y-axis scaling can also change the rendered shape. Duplicate or late single-node live notifications do not overwrite existing samples or counter rates. Series below the budget remain unchanged, and single-node graphs preserve sub-second timestamps; multi-node aggregation retains its existing one-second alignment.

This display reduction leaves the raw session cache intact. Active graphs also retain their source window so live updates do not repeatedly reduce previously sampled curves; these chart arrays are outside the cache memory estimate. Zoom uses the retained detail and cannot restore samples already discarded by collector downsampling.

### Graph Performance Settings

Open **Node Settings → Performance** to set **Graph drawing resolution** with a slider or compact numeric up/down from **0.1× to 2×**, then select **Apply**. The default is **1×**. This client-wide setting limits drawn points per series using the plotting width in logical pixels, independently of the configurable history budget (default 3×). A 750-pixel plot draws at most 75, 750, or 1,500 points at 0.1×, 1×, or 2× respectively. Very narrow plots retain a minimum of two points to draw a line.

Lower settings reduce line and fill geometry; higher settings retain more visible detail. Sampling preserves bucket endpoints and extrema. Zooming and panning sample the visible window again, retaining only the closest off-screen neighbours for edge continuity. Changing only drawing resolution redraws open charts without fetching history or changing the session cache.

Both numeric editors use a readable value field with small up/down arrows stacked on the right. You can type a value or use the arrows to step it.

**Retained history resolution** has its own slider and compact numeric up/down, ranging from **1× to 10×**, default **3×**. This controls how many points metric graphs retain per logical pixel for zooming and request from the collector. Higher values use more graph memory and can increase query size; lower values limit available zoom detail. Select **Apply** to save both settings. Changing history resolution refreshes open metric graphs after the existing debounce; raw cache retention remains controlled by the Collectors tab.

Both preferences persist in `~/.local/share/MADTOM/performance.json` (or the platform's equivalent application-data directory). Missing, malformed, or out-of-range values fall back independently to drawing 1× and history 3×. Older files without the history field use 3×:

```json
{"GraphPointsPerPixel": 1, "HistoryPointsPerPixel": 3}
```

### Graph Navigation: Zoom, Pan & Page Scrolling

Chart controls are optimized for intuitive pointer interaction without interfering with standard dashboard scrolling:
- **Normal Vertical Scroll Wheel**: Moves the page scrollbar up and down naturally. Pointer wheel events pass directly through to the enclosing `ScrollViewer`.
- **`Ctrl` + Scroll Wheel**: Engages pointer-anchored zooming on the chart hovered by the cursor (from $1.0\times$ up to $8.0\times$ magnification).
- **Horizontal Pan**: Click and drag horizontally across a zoomed chart to pan backward and forward across the time axis.

### Memory Normalization in Tooltips

When hovering over memory graphs or multi-metric series containing memory statistics (`memory.used`, `memory.total`, `memory.available`, `memory.swap`, etc.):
- Raw byte values are dynamically formatted into human-readable memory units:
  - **$\ge 1\,\text{TB}$**: Formatted in `TB` (e.g. `1.24 TB`).
  - **$\ge 1\,\text{GB}$**: Formatted in `GB` (e.g. `16.00 GB`).
  - **$\ge 1\,\text{MB}$**: Formatted in `MB` (e.g. `626.6 MB`).
  - **$\ge 1\,\text{KB}$**: Formatted in `KB` (e.g. `512 KB`).
  - **$< 1\,\text{KB}$**: Formatted in `B` (e.g. `512 B`).
- Handles rate-of-change derivatives signed formatting (e.g. `+12.4 MB/s` or `-5.2 MB/s`).

### Graph Groups, Side-by-Side Rows & Reordering

Rather than constraining dashboards to single-column stacks or fixed dual-column splits, MADTOM models layout rows as **Graph Groups**:
- **Dynamic Horizontal Columns**: Each row uses a responsive `<UniformGrid Rows="1" />`. Any number $N$ of graphs in a row divide horizontal viewport space equally ($N=1$: 100%, $N=2$: 50% each, $N=3$: 33.3% each, $N=4$: 25% each).
- **In-Place Card Controls**:
  - **`◀` / `▶` (Move Left / Right)**: Swaps graph positions horizontally within the current row.
  - **`▲` / `▼` (Move Up / Down)**: Moves the graph to the row above or below. When moving the only graph in a row, the entire row shifts up or down.
  - **`⤹ Row` (Separate to Row)**: Moves the selected graph out of a multi-graph row into its own standalone row.
  - **`Combine Row ↓`**: Merges the row below into the current row to display all their graphs side-by-side.
  - **`Split`**: Splits a multi-metric aggregated series into individual side-by-side charts within the same row.
- **Accidental Deletion Prevention**: To prevent accidentally dropping graphs during live monitoring, graph deletion buttons have been intentionally removed from live card headers. Graphs can only be safely removed from within the configuration modal.
- **Global Customization Modal**: Accessible via **⚙ Customize Graphs & Telemetry** on the top toolbar. When opened, the modal dims the entire Telemetry plugin viewport (`TelemetryRootView`), including navigation sidebars and headers. Within the modal, operators can configure graph groups, apply or save presets, adjust series colors, configure rate of change derivatives, and use one-click **Quick Merges**:
  - `Merge Disk I/O (R/W)`: Creates a unified read/write throughput chart (`disk.io.read_bytes` and `disk.io.write_bytes` with rate derivatives).
  - `Merge Network (In/Out)`: Creates a combined ingress/egress bandwidth chart (`network.ingress` and `network.egress`).
  - `CPU Breakdown`: Creates a 4-series CPU usage graph (`cpu.total`, `cpu.user`, `cpu.system`, `cpu.iowait`).
  - `Memory Breakdown`: Creates a RAM utilization graph (`memory.used`, `memory.total`, `memory.available`).

### Custom Color Wheel & Series ColorPicker

In the **Customize Graphs & Telemetry** modal, each series displays:
- **Preset Palette Swatches**: 8 quick-pick color buttons (Cyan, Emerald, Blue, Purple, Pink, Amber, Orange, Red).
- **Conic Multi-Color Wheel Button**: A circular rainbow sweep button with a multi-stop conic gradient brush. Clicking it opens Avalonia's interactive `ColorPicker` flyout, allowing operators to pick any RGB, HSV, or Hex color code.
- **Real-Time Color Updates**: Color adjustments immediately update series line pens, area gradient fills, and preview swatches across the active canvas charts and persist to `graphs.json`.

### Missing Data & Downtime Handling

When nodes go offline or newly added metrics (such as disk I/O) do not exist in earlier historical windows:
- Tooltips display `"N/A"` for missing or non-finite values (`NaN`, `+Infinity`, `-Infinity`).
- Gaps in telemetry (downtime $> 3$ minutes or $25\%$ of window width) are explicitly indicated with `N/A` rather than fabricating false zero values.
- Geometry coordinate mapping safely clamps invalid or empty points to the baseline without throwing exceptions or corrupting visual layout.

### Multi-Device Disk I/O Metrics & Diagnostics

MADTOM includes end-to-end disk I/O monitoring across the Go daemon scraper, protobuf stream, TSDB storage engine, and UI:
- Scraped directly from `/proc/diskstats` on Linux endpoints with sector calculation (1 sector = 512 bytes).
- Automatically filters out pseudo block devices (`loop*`, `ram*`) while capturing all physical drives and partitions (`sda`, `sda1`, `sdb`, `nvme0n1`, `nvme0n1p1`, etc.).
- Calculates physical whole-disk totals (`disk.io.read_bytes`, `disk.io.write_bytes`) to prevent double-counting sub-partitions, while exposing granular per-device series for targeted tracking.
- **Supported Metric Keys**:
  - `disk.io.read_bytes`: Total physical read throughput (bytes or bytes/sec).
  - `disk.io.write_bytes`: Total physical write throughput (bytes or bytes/sec).
  - `disk.io.read_ops`: Total physical read operations count.
  - `disk.io.write_ops`: Total physical write operations count.
  - Per-device metrics: `disk.io.<device>.read_bytes`, `disk.io.<device>.write_bytes`, `disk.io.<device>.read_ops`, `disk.io.<device>.write_ops` (e.g. `disk.io.sda.read_bytes`, `disk.io.sda1.read_bytes`, `disk.io.sdb.write_ops`).
  - Available dynamically in **Add Metric** dropdowns and in **Global Metrics** settings for top-bar pinning.

#### Troubleshooting Disk I/O on Cloud & ARM64 Instances

If a remote server (e.g. Oracle Cloud Infrastructure ARM64) displays no disk I/O values:
1. **Verify `/proc/diskstats`**:
   ```bash
   cat /proc/diskstats
   ```
   Check if whole-disk identifiers appear (e.g. `sda`, `sdb` for paravirtualized disks, `nvme0n1` for NVMe devices). MADTOM matches `^sd[a-z]+$` and `^nvme[0-9]+n[0-9]+$`. If disks are virtualized via device-mapper (`dm-0`), verify whether physical block devices are present.
2. **Inspect Block Hierarchy**:
   ```bash
   lsblk -o NAME,MAJ:MIN,RM,SIZE,RO,TYPE,MOUNTPOINTS
   ```
3. **Verify Daemon Binary & Service**:
   ```bash
   file /opt/madtomd
   systemctl status madtomd.service
   journalctl -u madtomd.service -n 50 --no-pager
   ```
   Ensure the running binary has been updated to the latest build supporting disk I/O telemetry and restarted via `deploy.sh`.

---

## Collector & Fleet Management

### Client History Cache

Open **Node Settings → Collectors → Client History Cache** to set how long this client retains streamed numeric graph history for all connected nodes. The default is **60 minutes**; enter **120** for two hours, or any whole number from **1 to 1440** minutes, then select **Apply**. Decreasing retention immediately removes older cached samples. Increasing it retains more future samples; it cannot recover monitor-only telemetry from before the client received it.

The read-only estimate textbox predicts memory from observed series counts and sample rates. It also shows approximate current buffer memory. Estimates include an allowance for queue capacity, but exclude chart copies, runtime overhead, and future changes in node/metric counts; they are not a memory limit. Before telemetry arrives, the estimate says it is waiting for data.

Monitor-only graph history now survives navigating away from a node and reopening its details during the same app session. Numeric per-core, NIC, disk, swap/zram, power, TWAMP, and process-name CPU series are included when received; full process snapshots and logs are not historical cache records. Unavailable or omitted metrics are not filled with stale values. Retention uses sample timestamps, and inactive series expire too.

History queries use local data for metrics currently configured as Monitor-only or Off. Stored metrics can use fully covered live windows (allowing up to five seconds between samples and at the live edge), otherwise collector history is merged with cached samples. Successful collector query results, including empty results, can be reused for 30 seconds. Local samples win at identical timestamps. Node configurations are cached for 30 seconds and refreshed immediately after successful settings changes made in this client. A collector failure still allows available local history to be displayed; cancellation remains cancellable.

**Clear cache** clears this client's live history and cached query results across all collectors. New streamed samples start filling it again. It does not delete collector storage or change node collection policies. Already-rendered chart arrays are refreshed when history reloads; they are not part of the cache. Cache contents disappear when the app exits. Only the retention preference persists, in `~/.local/share/MADTOM/telemetry-cache.json` (or the platform's equivalent application-data directory):

```json
{"RetentionMinutes":60}
```

Clicking the **Collector Settings (⚙)** icon in the fleet toolbar opens the multi-collector management panel:

### Opt-In Global Metrics & Top Bar Pinning

Rather than showing fixed static readouts, MADTOM features a fully configurable, opt-in global metrics system for fleet-wide monitoring:

- **Top-Bar Pinned Chips**: Pinned metrics appear in the top-right application header bar as interactive chips displaying the metric icon, short name, live formatted value, modifier badge (`SUM`, `AVG`, `Δ/s`), and a settings shortcut button. Clicking any pinned chip or the gear button immediately opens the **Global Metrics** tab in settings.
- **Aggregation Modifiers**:
  - **`Sum`**: Fleet-wide sum of metric values across all monitored nodes (e.g. aggregate ingress/egress bandwidth in Gbps/Mbps, total RAM bytes in GB, total disk IOPS).
  - **`Avg`**: Arithmetic mean across all active reporting nodes (e.g. average CPU load %, average TWAMP RTT latency in ms).
  - **`Rate of Change (/s)`**: Real-time second-by-second derivative ($\Delta / \text{sec}$) of the fleet total, displaying signed rates (`+`/`-`) with `/s` units.
- **Global Metrics Settings Tab (Tab 4)**:
  1. **Opt-In Configurator**: A metric dropdown selector paired with segmented modifier buttons (`Sum`, `Avg`, `Rate of Change (/s)`) and a **[+ Pin to Top Bar]** action button.
  2. **Currently Pinned**: A chips bar displaying all active top-bar items with live values and **[✕ Unpin]** buttons.
  3. **All Fleet Metrics Catalog**: Responsive cards for each available metric in the fleet catalog (`Network Ingress`, `Network Egress`, `CPU Load`, `RAM Used`, `Disk Read`, `Disk Write`, `Disk Operations`, `TWAMP Latency`). Each card shows live values, interactive modifier selectors, and a toggle pin button (**[+ Pin to Bar]** / **[✓ Pinned]**).
- **Persistent Storage**: Pinned selections, modifiers, and display order are persisted to `~/.local/share/MADTOM/global-metrics.json`.

### Dynamic Node Grouping & Fleet Filtering

Nodes are organized by custom group tags persisted in `node-groups.json`.
- **Group Assignment**: In the **Node Groups** tab of settings, operators can type custom group names or assign quick presets (`Compute`, `Storage`, `Edge`).
- **Clearing Group Assignments ("None")**: Clicking **None** clears the group tag (`""`), removing the host from `node-groups.json`. Unassigned nodes belong to the general pool and do not generate unnecessary filter pills.
- **Top-Right Filter Pills**: The fleet dashboard displays filter pills for **All** and all active custom groups. Unassigned nodes appear under **All**.
- **Strict Decoupling from 1Hz Telemetry Ticks**: Telemetry streaming providers stream purely hardware and OS metrics and have zero interaction with node groups. Group changes are driven exclusively by explicit user interactions via `NodeGroupStore.GroupChanged` events. This ensures that live 1Hz telemetry updates never overwrite user edits, resurrect removed groups (such as "Compute"), or cause input loss.
- **In-Place UI Reconciliation**: Both the fleet card grid and the node groups editor use in-place ViewModel reconciliation. On every 1Hz live telemetry tick, existing card and input containers are preserved. This completely eliminates hover flicker, card deselection, keyboard tab-loss, and typing interruptions.

### Node Opt-In Settings & Safe Apply Workflow

The **Node Opt-in Configuration** tab provides granular control over telemetry collection and client-side rendering switches:

- **Pinned Node Selector**: The `Select Node:` selector is pinned to the top of the tab container outside the scroll viewer, ensuring it remains permanently visible and never scrolls out of view regardless of pane height.
- **Default Selection & Clear Option**: The dropdown defaults to **None**, showing a helpful placeholder card until an operator deliberately selects a target host. A **[✕ Clear]** button allows returning to the unselected state at any time.
- **Single Source of Truth**: Selecting a host queries the collector and remote daemon via `GetNodeConfigAsync` to fetch its actual active configuration, preventing stale UI switch states.
- **3-Tier Opt-In List Mode**: Metrics are organized into grouped categories (`CPU Load (Overall)`, `Per-Core CPU Load`, `Basic Memory (RAM)`, `Swap Partitions`, `ZRAM Compressed RAM`, `Network Interfaces (NICs)`, `Disk Block Devices`, `Power & Battery`, `TWAMP Light Latency`). Each group features a 3-state radio button selector:
  - **`Off`**: Completely disables sampling.
  - **`Monitor`**: 1 Hz live streaming for dashboard viewing with zero disk writes (neither on daemon WAL nor in collector TSDB).
  - **`Store`**: 1 Hz live streaming and durable persistence in Pebble TSDB (and WAL spooling during offline periods).
- **Granular Device Expanders**: Expandable sections beneath multi-device categories allow setting overrides for specific hardware entities—such as individual NICs (`eth0`, `docker0`), specific CPU cores (`Core 0` ... `Core N`), individual swap devices, and multi-device ZRAM instances (`zram0`, `zram1`).
- **Interactive Graph Opt-In Prompt**: When adding a graph in the **Customize Graphs** modal for a metric that is currently `Off` on the target host, MADTOM displays an interactive modal card right away (`[Monitor Only]` vs. `[Monitor & Store]`). Selecting a mode automatically sends `UpdateNodeConfigAsync` upstream via gRPC to reconfigure the daemon and instantiates the graph in a single seamless action.
- **Unapplied Changes Badge**: Any modification to switches, inputs, or sliders immediately flags the state as dirty and displays an amber **`● Unapplied Changes`** badge next to the **[Apply to Node]** button.
- **Confirmation & Revert on Close**: If the modal is closed (via `✕`, backdrop click, or the `Esc` key) while unapplied changes exist, MADTOM presents a confirmation dialog preventing accidental loss of configuration:
  - **Apply & Close**: Pushes the modified settings to the daemon, captures the new baseline, and dismisses the dialog.
  - **Discard Changes**: Reverts all UI switches and inputs back to the daemon's active baseline and dismisses the dialog.
  - **Cancel**: Aborts closing and keeps the configuration tab open with all edits intact.

### Adding a Collector
Enter a display name and `host:port` address (e.g. `10.0.0.15:50051`), then click **+ Add Collector**. The UI immediately establishes a gRPC connection, triggers discovery, and saves the endpoint to `collectors.json`.

### Editing a Collector
Clicking any existing collector in the list enters edit mode: the name and address are loaded into the input fields, and the button changes to **Save Changes**. Clicking **Save Changes** updates the endpoint in-place and reconnects without losing node state. Clicking **Cancel** reverts to add mode.

### Removing a Collector
Click the trash icon next to any configured collector to close its channel and remove it from persistence.

### Multi-Hub Aggregation
When multiple collectors are configured, their nodes are discovered concurrently and aggregated onto the fleet dashboard seamlessly.

---

## Process Monitoring, Storage Modes & Graphing

MADTOM implements an adaptive 3-tier collection and storage architecture for process-level telemetry to balance operational visibility against CPU and network overhead.

### Three-Tier Collection Policy

Process telemetry policies are configured per-node in the **Collector Endpoints & Node Settings** modal (`Node Opt-in Configuration` tab under Tier 1 Opt-in):

| Mode | Daemon `/proc` Probing | gRPC Streaming | Pebble TSDB Storage | Description |
|---|---|---|---|---|
| **`Disabled (Off)`** | ❌ Stopped | ❌ Zero bytes | ❌ None | Completely halts process scanning on the host daemon. Conserves CPU cycles and network bandwidth. The UI Processes tab displays an informative disabled banner. |
| **`Live Only (Probed)`** | ✅ 1Hz Snapshot | ✅ Ephemeral | ❌ None (0 bytes) | Probes system processes once per second for interactive inspection in the Processes tab. Data is kept in memory and discarded upon arrival, generating zero disk writes in TSDB. **(Default)** |
| **`Probed & Stored (TSDB)`** | ✅ 1Hz Snapshot | ✅ Streamed | ✅ Top $N$ + Other | Scrapes 1Hz process snapshots, calculates Top $N$ CPU consumers, and commits them as historical time-series metrics into Pebble TSDB for scoped charting. |

### Top-N Process Count Configuration

When **`Probed & Stored`** is selected, operators can configure the exact number of top processes saved to TSDB using an interactive slider:
- **Range**: **1 to 10 processes** (default: `5`).
- **Granularity**: Snap-to-integer slider with real-time numeric readout (`Top 5 processes`).
- **Dynamic Aggregation**: Processes are grouped and aggregated by sanitized executable name (e.g. `postgres`, `mysqld`, `dotnet`) rather than volatile OS PIDs to avoid unbounded TSDB key cardinality.
- **Remainder Metric (`proc.cpu.other`)**: Any CPU consumption from processes outside the configured Top $N$ is automatically aggregated into `proc.cpu.other = total_cpu - sum(top_N)`, guaranteeing that the sum of process breakdown metrics always accounts for 100% of host CPU usage.

### Historical TSDB Metric Storage & Breakdown Charting

When process storage is enabled, the collector ingestion pipeline creates structured time-series metrics:
- **Metric Keys**:
  - `proc.cpu.<executable>`: Total CPU % consumed by all instances of the process.
  - `proc.cpu.other`: Total CPU % consumed by all remaining background processes.
- **Process Breakdown Chart (`process.breakdown`)**:
  - To prevent unbounded list growth as top processes change over time, individual `proc.cpu.<name>` metrics are not added one-by-one to the metrics dropdown.
  - Instead, **Customize Graphs & Telemetry** provides a single aggregate metric: **`process.breakdown` ("Process Breakdown")**.
  - Selecting `process.breakdown` and clicking **[+ Add Graph]**, or clicking the **`Merge Process Breakdown (Top + Total)`** quick action button, automatically constructs a unified multi-series chart containing `cpu.total`, the host's active top recorded processes with distinct colors, and `proc.cpu.other`.
  - Supports all historical time scopes (`1m`, `5m`, `30m`, `2h`, `6h`, `12h`, `24h`, and custom date/time range) with LTTB downsampling.
- **Custom Process Metric (`custom.process`)**:
  - For targeting specific individual processes without cluttering the base dropdown, **`custom.process` ("Custom Process...")** is available in the metric selector.
  - Clicking **[+ Add Graph]** opens a searchable dialog displaying up to the top 1,000 active processes across the node/cluster, with instant filtering by PID, process name, or user.
  - Confirming adds a dedicated `proc.cpu.<sanitized_name>` graph to the dashboard.
- **Button Auto-Wrapping**:
  - Preset rows, metric actions, quick merge buttons, and graph card action buttons in the Customize Graphs modal utilize auto-wrapping containers (`WrapPanel`) so controls cleanly wrap without overflowing on narrow or scaled viewports.

### Processes Manager: Sub-Tabs & True Top N Overview

The **Processes** tab features a streamlined navigation bar with two specialized views:

1. **List** (Live Process Snapshot):
   - **Responsive Flex Toolbar**: Uses a responsive `WrapPanel` where sub-tab toggles (`List` / `Overview`), compact search filter (`130px`, halved width), `↻ Refresh` button, and `Target: [hostId]` badge sit cleanly on one row on desktop and wrap only when viewport width is constrained.
   - **Compact Layout**: Clean padding and tight row spacing without redundant nested container borders.
   - **Live Process Table**: Streamed process snapshots with PID, command name, user, thread count, CPU %, and memory usage.
   - **UI Virtualization**: Uses a strictly bounded viewport with virtualized row containers (`VirtualizingStackPanel`). Only rows visible within the viewport are materialized into memory, ensuring smooth 60 FPS scrolling and zero unconstrained scrollviewer expansion even when rendering 100+ processes per page.
   - **Sorting & Filtering**: Real-time regex/substring filtering across PID, process name, and user.
   - **Disabled State Banner**: When process collection is set to `Disabled`, the table displays an overlay explaining that probing is turned off to save resources, with directions to re-enable in Node Opt-in settings.
   - **Process Signals**: Signal delivery buttons (`SIGTERM`, `SIGKILL`) are safety-gated and disabled for collector-backed remote nodes.

2. **Overview (TRUE Top N Dynamic Chart & Scopes)**:
   - **Standard Scopes Toolbar**: Features the full telemetry scope selector (`1m`, `5m` [default], `30m`, `2h`, `6h`, `12h`, `24h`, `Custom`) directly above the chart alongside the configurable Top $N$ slider.
   - **Continuous Sliding Window**: Window bounds continuously slide leftward as time progresses (`WindowStart = WindowEnd - ScopeSpan`) without being artificially pinned to sample zero.
   - **Immediate Line Rendering**: Initial snapshot is seeded at $T - 1\text{s}$ so geometry cache generates lines and filled areas immediately without requiring a waiting period.
   - **Historical TSDB Querying**: Automatically queries stored `proc.cpu.*` metrics across the selected time scope on scope changes or node selection.
   - **Dynamic Rank Tracking**: Displays an aggregated historical chart rendered with `MetricHistoryChartControl` tracking the true top $N$ processes over the selected scope window.
   - **Configurable Top N**: Uses an interactive slider (ranging from 1 to 10 ranks) with live updates.
   - **Identity Preservation Across Process Churn**: Rather than pinning lines to static process names (which drop to 0% when a process terminates), each series tracks **#1, #2, ... #N**. As top processes exit and new processes emerge, the rank curve remains continuous with a consistent palette color.
   - **Per-Point Timestamp Hover Tooltips**: Hovering over any timestamp on the chart displays the true process name, rank, and CPU percentage recorded at that exact point in time via per-point label annotations.
   - **Active Leaders Footer**: A wrap panel below the chart highlights the current leader process name, rank badge, and CPU% for each active rank.

---

## Theming & Display Contrast Profiles

MADTOM Console includes a comprehensive real-time theme engine supporting standard dark palettes, daytime light modes, and specialized contrast compensation profiles for problematic display hardware (e.g. cheap IPS/TFT panels with backlight bleed).

### Accessing Theme & Lexicon Settings

Click the **Gear Icon (⚙)** on the top left of the MADTOM Console header, immediately to the right of the MADTOM Console logo. This opens the unified Settings flyout menu:
- **Theme Selection**: Switch between all 8 visual themes.
- **Lexicon Selection**: Switch between Goose, Feline, and Standard terminology dialects. Redundant top-bar selectors have been eliminated in favor of this single centralized menu.
- **Persistence**: All selections are automatically written to `~/.local/share/MADTOM/settings.json` and restored on next launch.

Theme changes immediately propagate to:
1. **MADTOM Console Host**: Top bar, navigation sidebar, active module badges, and dialogs.
2. **Loaded Plugins (e.g. MADTOM Telemetry)**: All canvas charts, radar plots, node detail views, and sparklines.
3. **Avalonia Native Controls**: The window dynamically toggles between `ThemeVariant.Dark` and `ThemeVariant.Light` based on background luminance, ensuring native scrollbars, menus, and text selection match.

### Built-in Palettes

| Theme Key | Display Name | Category | Primary Background | Accent Color | Description |
|---|---|---|---|---|---|
| `default-dark` | **Default Dark** | Dark | `#070A12` | `#06B6D4` (Cyan) | Modern high-density cyberpunk dark theme. |
| `pure-light` | **Pure Light** | Light | `#F8FAFC` | `#0284C7` (Sky Blue) | Crisp, high-clarity daylight theme designed for bright office environments. |
| `paper-white` | **Paper White** | Light | `#FBF9F5` | `#B45309` (Amber Brown) | Warm sepia low-glare reading profile reducing eye strain. |
| `minimal-mono` | **Minimal Mono** | Minimal | `#121214` | `#E4E4E7` (Slate Silver) | Distraction-free monochromatic dark palette with muted saturation. |
| `anti-bleed-grey` | **IPS Neutralizer** | IPS / Anti-Bleed | `#23272E` | `#00D4FF` (Electric Cyan) | Lifted charcoal gray background specifically calibrated to mask severe IPS corner glow, edge bleed, and backlight unevenness. |
| `tft-amber-terminal` | **TFT Amber CRT** | TFT / High-Angle | `#1B1C18` | `#FFB000` (Amber Phosphor) | High-contrast amber CRT profile optimized for low-contrast TFT displays and extreme off-axis viewing angles. |
| `high-contrast` | **High Contrast** | High Contrast | `#000000` | `#00F0FF` (Neon Cyan) | True black OLED palette with ultra-high contrast ratio and vivid neon indicators. |
| `solarized-dark` | **Solarized Dark** | Dark | `#002B36` | `#2AA198` (Solarized Cyan) | Ethan Schoonover's precision-engineered solarized palette. |

---

### Dynamic Theme Loading & File Watching

MADTOM uses a fully dynamic, decoupled theme architecture. Rather than hardcoding theme names or switch-cases in C# code:
1. **Console Fallback Palettes**: The Host (`MADTOM.Console`) owns all global base/fallback themes (`Assets/Themes/*.json`).
2. **Runtime User Themes**: At startup, MADTOM scans the `~/.local/share/MADTOM/themes/` directory for any `.yaml`, `.yml`, or `.json` files.
3. **Hot Reloading**: An active `FileSystemWatcher` monitors the themes folder. Creating, modifying, or deleting a theme file automatically reloads the theme catalog and refreshes the UI Settings dropdown in real time.
4. **Zero-Dependency YAML Parsing**: Custom `.yaml` and `.yml` theme files are parsed using a built-in, lightweight stateful parser with zero third-party dependencies, preserving comments and supporting quoted hex color strings.

---

### Plugin Theme Overrides

When an operator switches themes in the Console, the host applies the base fallback palette and signals loaded plugins via `IThemeHost`. Plugins (such as `MADTOM.Plugins.Telemetry`) resolve their active palette using a tiered override hierarchy:

1. **Host Fallback Palette**: The base colors defined in Console's built-in themes or global `~/.local/share/MADTOM/themes/`.
2. **Plugin Asset Overrides**: If the plugin contains a theme file with the matching name in its embedded assets (`avares://MADTOM.Plugins.Telemetry/Assets/Themes/{themeName}.*`), any defined colors override the host fallback colors for that plugin.
3. **Plugin Directory Overrides**: If a theme file exists in `~/.local/share/MADTOM/themes/telemetry/{themeName}.yaml` (or `.yml` / `.json`), its values take the highest priority and override both plugin asset and host fallback colors.

> [!TIP]
> Override files only need to declare the specific colors they wish to alter (e.g. specialized radar brushes, chart grid lines, or card backgrounds). All unmentioned colors automatically retain their host fallback values.

---

### Custom Theme Schema & Example

To create a custom theme, place a `.yaml` or `.json` file in `~/.local/share/MADTOM/themes/` (e.g. `my-theme.yaml`).

#### YAML Schema Example (`example-synthwave.yaml`)

```yaml
# MADTOM Custom Theme Definition
themeName: synthwave-84
displayName: Synthwave '84
category: Custom

colors:
  # Base Surface Colors
  Background: "#1A102F"
  Surface: "#241744"
  SurfaceSubtle: "#2E1E54"
  SurfaceHover: "#3D296E"
  Border: "#4D3282"
  BorderSubtle: "#3A2663"

  # Typography
  TextPrimary: "#FFFFFF"
  TextSecondary: "#E2D9F3"
  TextMuted: "#9D8BB5"

  # Accents & Highlights
  Accent: "#FF7EDB"
  AccentSubtle: "#3B2252"
  AccentHover: "#FF9CE6"
  AccentGlow: "#4DFF7EDB"

  # Semantic Indicators
  Success: "#36F9C7"
  Warning: "#FFE600"
  Error: "#FE4450"
  Info: "#2DE2E6"

  # Canvas Visualizer & Radar
  GridLine: "#2E1E54"
  Crosshair: "#FF7EDB"
  RadarArea: "#33FF7EDB"
  RadarEdge: "#FF7EDB"
```

#### JSON Schema Example (`my-theme.json`)

```json
{
  "themeName": "nord-frost",
  "displayName": "Nord Frost",
  "category": "Custom",
  "colors": {
    "Background": "#2E3440",
    "Surface": "#3B4252",
    "SurfaceSubtle": "#434C5E",
    "SurfaceHover": "#4C566A",
    "Border": "#4C566A",
    "BorderSubtle": "#3B4252",
    "TextPrimary": "#ECEFF4",
    "TextSecondary": "#E5E9F0",
    "TextMuted": "#D8DEE9",
    "Accent": "#88C0D0",
    "AccentSubtle": "#2E3F4D",
    "AccentHover": "#8FBCBB",
    "AccentGlow": "#3388C0D0",
    "Success": "#A3BE8C",
    "Warning": "#EBCB8B",
    "Error": "#BF616A",
    "Info": "#81A1C1",
    "GridLine": "#3B4252",
    "Crosshair": "#88C0D0",
    "RadarArea": "#3388C0D0",
    "RadarEdge": "#88C0D0"
  }
}
```

> [!TIP]
> If `displayName` is omitted, MADTOM automatically derives a title-cased display name from `themeName` or the filename (e.g. `synthwave-84` becomes `Synthwave 84`). Missing colors automatically fall back to dark-theme defaults.
