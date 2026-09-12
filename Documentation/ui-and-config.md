# UI Guide & Configuration Reference

This guide covers the MADTOM Desktop Operator UI, its features, telemetry visualization capabilities, and persistent configuration stores.

---

## Table of Contents

1. [Desktop UI Overview](#desktop-ui-overview)
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
   - [Relative Scopes (1m, 5m, 30m, 2h, 6h, 12h, 24h)](#relative-scopes-1m-5m-30m-2h-6h-12h-24h)
   - [Custom Scope (Date & Time Picker)](#custom-scope-date--time-picker)
   - [Resolution-Adaptive Downsampling (LTTB)](#resolution-adaptive-downsampling-lttb)
   - [Graph Navigation: Zoom, Pan & Page Scrolling](#graph-navigation-zoom-pan--page-scrolling)
   - [Graph Groups, Side-by-Side Rows & Reordering](#graph-groups-side-by-side-rows--reordering)
   - [Missing Data & Downtime Handling](#missing-data--downtime-handling)
   - [Disk I/O Read/Write Metrics](#disk-io-readwrite-metrics)
4. [Collector & Fleet Management](#collector--fleet-management)
   - [Opt-In Global Metrics & Top Bar Pinning](#opt-in-global-metrics--top-bar-pinning)
   - [Dynamic Node Grouping & Fleet Filtering](#dynamic-node-grouping--fleet-filtering)
   - [Adding a Collector](#adding-a-collector)
   - [Editing a Collector](#editing-a-collector)
   - [Removing a Collector](#removing-a-collector)
   - [Multi-Hub Aggregation](#multi-hub-aggregation)
5. [Process Monitoring & Signals](#process-monitoring--signals)
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
    - **Metrics Tab**: Real-time canvas graphs with zoom, pan, hover tooltips, and customizable multi-metric series.
    - **Processes Tab**: Live process tree, thread counts, and memory/CPU sorting.
    - **TWAMP Flight Tab**: Asymmetry radar and round-trip flight times.
    - **Logs Tab**: Node-level activity logs.

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
  "IsTelemetrySidebarCollapsed": false
}
```

- **`Theme`**: Active theme profile key (e.g. `default-dark`, `pure-light`, `paper-white`, `minimal-mono`, `anti-bleed-grey`, `tft-amber-terminal`, `high-contrast`, `solarized-dark`).
- **`Language`**: Active lexicon terminology dialect (`goose`, `feline`, or `standard`).
- **`IsConsoleSidebarCollapsed`**: Navigation rail collapse state for the `MADTOM.Console` host.
- **`IsTelemetrySidebarCollapsed`**: Navigation rail collapse state for the Telemetry plugin module.
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
When querying historical ranges with tens of thousands of data points, `madtom-collector` applies **Largest-Triangle-Three-Buckets (LTTB)** downsampling:
- Reduces raw points down to an optimal visual budget (default: 1,200 points).
- Accurately preserves visual peaks, valleys, outliers, and trend lines without flattening spikes.

### Graph Navigation: Zoom, Pan & Page Scrolling

Chart controls are optimized for intuitive pointer interaction without interfering with standard dashboard scrolling:
- **Normal Vertical Scroll Wheel**: Moves the page scrollbar up and down naturally. Pointer wheel events pass directly through to the enclosing `ScrollViewer`.
- **`Ctrl` + Scroll Wheel**: Engages pointer-anchored zooming on the chart hovered by the cursor (from $1.0\times$ up to $8.0\times$ magnification).
- **Horizontal Pan**: Click and drag horizontally across a zoomed chart to pan backward and forward across the time axis.

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
- **Global Customization Modal**: Accessible via **⚙ Customize Graphs & Telemetry** on the top toolbar. When opened, the modal dims the entire Telemetry plugin viewport (`TelemetryRootView`), including navigation sidebars and headers. Within the modal, operators can configure graph groups, apply or save presets, adjust series colors, and configure rate of change derivatives.

### Missing Data & Downtime Handling

When nodes go offline or newly added metrics (such as disk I/O) do not exist in earlier historical windows:
- Tooltips display `"N/A"` for missing or non-finite values (`NaN`, `+Infinity`, `-Infinity`).
- Gaps in telemetry (downtime $> 3$ minutes or $25\%$ of window width) are explicitly indicated with `N/A` rather than fabricating false zero values.
- Geometry coordinate mapping safely clamps invalid or empty points to the baseline without throwing exceptions or corrupting visual layout.

### Disk I/O Read/Write Metrics

MADTOM includes end-to-end disk I/O monitoring across the Go daemon scraper, protobuf stream, TSDB storage engine, and UI:
- Scraped directly from `/proc/diskstats` on Linux endpoints with sector calculation (1 sector = 512 bytes).
- Automatically filters out individual partitions to prevent double-counting totals.
- **Supported Metric Keys**:
  - `disk.io.read_bytes`: Total read throughput (bytes or bytes/sec).
  - `disk.io.write_bytes`: Total write throughput (bytes or bytes/sec).
  - `disk.io.read_ops`: Total read operations count.
  - `disk.io.write_ops`: Total write operations count.
  - Per-device metrics: `disk.io.<device>.read_bytes`, `disk.io.<device>.write_bytes`, `disk.io.<device>.read_ops`, `disk.io.<device>.write_ops` (e.g. `disk.io.sda.read_bytes`).

---

## Collector & Fleet Management

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

### Adding a Collector
Enter a display name and `host:port` address (e.g. `10.0.0.15:50051`), then click **+ Add Collector**. The UI immediately establishes a gRPC connection, triggers discovery, and saves the endpoint to `collectors.json`.

### Editing a Collector
Clicking any existing collector in the list enters edit mode: the name and address are loaded into the input fields, and the button changes to **Save Changes**. Clicking **Save Changes** updates the endpoint in-place and reconnects without losing node state. Clicking **Cancel** reverts to add mode.

### Removing a Collector
Click the trash icon next to any configured collector to close its channel and remove it from persistence.

### Multi-Hub Aggregation
When multiple collectors are configured, their nodes are discovered concurrently and aggregated onto the fleet dashboard seamlessly.

---

## Process Monitoring & Signals

The **Processes** tab inspects real-time process statistics scraped by the daemon:
- **Columns**: PID, Process Name, CPU %, RSS Memory, Thread Count, User.
- **UI Virtualization**: Uses a strictly bounded viewport with virtualized row containers (`VirtualizingStackPanel`). Only rows visible within the viewport are materialized into memory, ensuring smooth 60 FPS scrolling and zero unconstrained scrollviewer expansion even when rendering 100+ processes per page.
- **Sorting & Filtering**: Real-time regex/substring filtering across PID, process name, and user.
- **Process Signals**: Signal delivery buttons (`SIGTERM`, `SIGKILL`) are safety-gated and disabled for collector-backed remote nodes.

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


