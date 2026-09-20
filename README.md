# MADTOM: Modular Engineering & Operations Studio

MADTOM is an extensible operations and systems engineering workspace built around **MADTOM Studio**—a high-performance, modular desktop host shell that manages and unifies specialized plugins across telemetry, media transcoding, network analysis, device automation, and AI operations.

```mermaid
graph LR
    subgraph Monitored & Managed Infrastructure
        D1["madtom-daemon<br/>(Push Mode)"]
        D2["madtom-daemon<br/>(Pull Mode)"]
        D3["madtom-daemon<br/>(Reverse-Push Mode)"]
        SQD["squeeze-daemon<br/>(Transcode Engine)"]
    end

    subgraph Central Telemetry Hub
        C["madtom-collector<br/>(gRPC Port 50051)<br/>Pebble TSDB Storage"]
    end

    subgraph Host Shell & Operator Clients
        UI["MADTOM.Studio<br/>(Unified Avalonia Desktop Shell)"]
        APP1["MADTOM.Plugins.Telemetry.App<br/>(Standalone Telemetry)"]
        APP2["MADTOM.Plugins.Squeeze.App<br/>(Standalone SQUEEZE)"]
    end

    D1 -->|"gRPC Push Stream"| C
    C -->|"gRPC Scrape (1Hz)"| D2
    C -->|"Reverse Connection"| D3
    D3 -.->|"Streams Over Channel"| C
    C -->|"1Hz Live Stream & Queries"| UI
    C -->|"1Hz Live Stream & Queries"| APP1
    SQD -->|"Transcode RPC / API"| UI
    SQD -->|"Transcode RPC / API"| APP2
```

---

## Documentation Index

Comprehensive guides, manuals, and technical deep-dives are organized into modular subsystems under the [`Documentation/`](Documentation/) directory:

### MADTOM Studio (Host Application)
- 🖥️ **[Studio UI & Theming Guide](Documentation/Studio/overview-and-ui.md)**: Desktop Operator UI walkthrough, layout scaling multiplier (10% to 1000%), system tray host & menu, runtime theming engine (14 palettes, hot-reload, YAML/JSON schemas), lexicon dialect system, and `settings.json`.
- 🔌 **[Plugin Architecture Reference](Documentation/Studio/plugin-architecture.md)**: Extensible plugin system contracts (`MADTOM.PluginContracts`), `IPluginModule`, `ITrayMenuService`, host context, and step-by-step guide for creating new plugins in `src/Plugins/`.

### MADTOM Telemetry Plugin
- 📊 **[Telemetry UI & Visualization Guide](Documentation/Plugins/Telemetry/ui-and-visualization.md)**: Fleet dashboard, live 1Hz sparklines, node detail, graph scopes, LTTB downsampling, process monitoring & breakdown charts, TWAMP latency radar, cache diagnostics, and configs (`graphs.json`, `collectors.json`).
- ⚙️ **[Architecture & Protocols Reference](Documentation/Plugins/Telemetry/architecture-and-protocols.md)**: Ingestion topologies (Push, Pull, Reverse-Push), Write-Ahead Log (WAL) disk spooling, CockroachDB Pebble TSDB layout, and TWAMP Light probing (RFC 5357).
- 🚀 **[Deployment & Services Guide](Documentation/Plugins/Telemetry/deployment-and-services.md)**: Remote SSH deployment automation (`deploy.sh`), architecture auto-detection, systemd units, and security sandboxing (`CAP_NET_BIND_SERVICE`).

### Modular Plugins
- 🗜️ **[MADTOM: SQUEEZE](Documentation/Plugins/Squeeze/README.md)**: Hardware-accelerated media transcode toolkit, remote daemon distribution, queue prioritization, and transcode telemetry.
- 📱 **[MADTOM: Android Toolkit](Documentation/Plugins/AndroidToolkit/README.md)**: Device backup, ADB command dispatching, package deployment, and battery/storage health telemetry.
- 📺 **[MADTOM: MediaCenter](Documentation/Plugins/MediaCenter/README.md)**: Local & network NAS streaming, direct play library indexer, and DLNA/UPnP playback control.
- 🧠 **[MADTOM: NOX AI Control Center](Documentation/Plugins/NoxAI/README.md)**: Local LLM orchestrator, GPU inference daemon monitor (Ollama/vLLM), and context memory pipeline.
- 🔊 **[MADTOM: AUDIOSYNC](Documentation/Plugins/AudioSync/README.md)**: Precision multi-room audio synchronization, PTP master clock disciplining, and latency buffer calibration.
- 🌐 **[MADTOM: VNA](Documentation/Plugins/VNA/README.md)**: Visual Network Analyzer for real-time topology mapping, latency graphs, interface load metrics, and packet flow diagnostics.
- 🔌 **[MADTOM: Connection Toolkit](Documentation/Plugins/ConnectionToolkit/README.md)**: Unified connection manager and proxy utility for tunneling, SSH key management, port forwarding, and protocol bridges.

### Scripts & CLI Tools
- 📖 **[CLI & Scripts Reference](Documentation/cli-and-scripts.md)**: Exhaustive reference of all CLI arguments, flags, scripts (`build.sh`, `publish.sh`, `deploy.sh`), and binary execution commands.

---

## Quick Start

### 1. Build All Projects Locally
```bash
# Build Go backend daemons and compile .NET solution
./build.sh

# Or build with Release optimizations & stripped symbols
./build.sh --release
```
*See [Build Script Options](Documentation/cli-and-scripts.md#1-buildsh--unified-project-build) for multi-arch cross-compilation.*

### 2. Publish Self-Contained .NET Linux App
```bash
# Publish single-file executables to MADTOM_DOTNET/publish/linux-x64/
./publish.sh

# Or cross-publish for ARM64 Linux (Raspberry Pi 4/5, AWS Graviton)
./publish.sh --arch arm64
```
*See [.NET Publishing Guide](Documentation/cli-and-scripts.md#2-publishsh--net-self-contained-linux-publishing) for RID aliases and project filters.*

For UI baselines, launch with `MADTOM_UI_TIMING=1` and summarize the captured stderr log with `python3 tools/Telemetry/summarize_ui_performance.py ui-performance.log`. See [capture instructions and metric definitions](Documentation/Plugins/Telemetry/ui-and-visualization.md#ui-performance-baselining).

### 3. Deploy Go Backend Daemon via SSH
```bash
# Deploys binary, auto-detects architecture (x86/ARM), installs with sudo, restarts service
./deploy.sh user@node.example.com
```
*See [Remote Deployment Guide](Documentation/Plugins/Telemetry/deployment-and-services.md#remote-ssh-deployment-deploysh) for details on staging and sudo automation.*

---

## At a Glance: Commands & Tools

### Scripts Cheat Sheet
| Script | Primary Usage | Full Reference |
|---|---|---|
| [`./build.sh`](build.sh) | Build Go daemons & .NET solution (`--release`, `--test`, `--arch`, `--test-plugin`) | [Documentation](Documentation/cli-and-scripts.md#1-buildsh--unified-project-build) |
| [`./publish.sh`](publish.sh) | Self-contained single-file Linux publisher (`--arch arm64`, `-p studio`, `--clean`) | [Documentation](Documentation/cli-and-scripts.md#2-publishsh--net-self-contained-linux-publishing) |
| [`./deploy.sh`](deploy.sh) | Remote SSH/sudo installer with auto-arch probe (`-a auto`, `--build`) | [Documentation](Documentation/Plugins/Telemetry/deployment-and-services.md#remote-ssh-deployment-deploysh) |
| [`MADTOM_GOLANG/build.sh`](MADTOM_GOLANG/build.sh) | Standalone Go compiler for `amd64`, `arm64`, and `armv7` | [Documentation](Documentation/cli-and-scripts.md#4-madtom_golangbuildsh--go-multi-architecture-compiler) |

### Executables Cheat Sheet
| Binary | Role | Full Reference |
|---|---|---|
| `madtom-daemon` | Lightweight endpoint agent (CPU, memory, disk, network, TWAMP) | [Documentation](Documentation/cli-and-scripts.md#1-madtom-daemon--node-telemetry-agent) |
| `madtom-collector` | Central gRPC telemetry hub, Pebble TSDB storage, LTTB queries | [Documentation](Documentation/cli-and-scripts.md#2-madtom-collector--central-telemetry-hub) |
| `MADTOM.Studio` | Modular desktop operator workspace & plugin host shell | [Documentation](Documentation/Studio/overview-and-ui.md) |
| `MADTOM.Plugins.Telemetry.App` | Standalone telemetry client without studio shell | [Documentation](Documentation/Plugins/Telemetry/ui-and-visualization.md#overview--standalone-mode) |
| `MADTOM.Plugins.Squeeze.App` | Standalone SQUEEZE media transcode client | [Documentation](Documentation/Plugins/Squeeze/README.md) |
| `MADTOM.Plugins.AndroidToolkit.App` | Standalone Android toolkit runner | [Documentation](Documentation/Plugins/AndroidToolkit/README.md) |
| `MADTOM.Plugins.MediaCenter.App` | Standalone MediaCenter runner | [Documentation](Documentation/Plugins/MediaCenter/README.md) |
| `MADTOM.Plugins.NoxAI.App` | Standalone NOX AI control center runner | [Documentation](Documentation/Plugins/NoxAI/README.md) |
| `MADTOM.Plugins.AudioSync.App` | Standalone AUDIOSYNC runner | [Documentation](Documentation/Plugins/AudioSync/README.md) |
| `MADTOM.Plugins.VNA.App` | Standalone Visual Network Analyzer runner | [Documentation](Documentation/Plugins/VNA/README.md) |
| `MADTOM.Plugins.ConnectionToolkit.App` | Standalone Connection Toolkit runner | [Documentation](Documentation/Plugins/ConnectionToolkit/README.md) |

---

## Configuration & Storage Summary

All UI settings and layouts persist across application launches:

- **Host Preferences**: `~/.local/share/MADTOM/settings.json` (stores theme selection, lexicon dialect, UI scale, tray behavior).
- **Custom Themes**: `~/.local/share/MADTOM/themes/` (drop-in YAML / JSON theme definitions with live hot reload).
- **Graph Layouts**: `~/.local/share/MADTOM/graphs.json` (stores custom cards, metrics, and colors per node).
- **Graph Presets**: `~/.local/share/MADTOM/graph-presets.json` (stores named multi-graph dashboard presets).
- **Saved Collectors**: `~/.local/share/MADTOM/collectors.json` (stores configured collector hub endpoints).
- **Node Groups**: `~/.local/share/MADTOM/node-groups.json` (stores environment and custom fleet groupings).
- **Time-Series Database**: `/var/lib/madtom/collector_data` (embedded CockroachDB Pebble TSDB).

*For a complete walkthrough of configuration formats, see the [Studio UI Guide](Documentation/Studio/overview-and-ui.md#host-configuration--persistent-storage) and [Telemetry UI Guide](Documentation/Plugins/Telemetry/ui-and-visualization.md).*

---

## Repository Layout

```text
MADTOM/
├── build.sh                      # Root unified build script (Go + .NET, with --test-plugin)
├── publish.sh                    # Symlink to MADTOM_DOTNET/publish.sh
├── deploy.sh                     # Symlink to MADTOM_GOLANG/deploy.sh
├── README.md                     # High-level entry point & documentation index
├── Documentation/                # In-depth technical guides & manuals
│   ├── cli-and-scripts.md        # CLI flags, parameters, and executable usages
│   ├── Studio/                   # MADTOM Studio (Host Application)
│   │   ├── overview-and-ui.md    # UI scaling, theming engine, tray host, settings
│   │   └── plugin-architecture.md# Plugin contracts, IPluginModule, ITrayMenuService
│   └── Plugins/                  # Modular Plugin Documentation
│       ├── Telemetry/            # Telemetry Plugin guides
│       ├── Squeeze/              # SQUEEZE transcode toolkit guides & architecture
│       ├── AndroidToolkit/       # Android Toolkit guide
│       ├── MediaCenter/          # MediaCenter guide
│       ├── NoxAI/                # NOX AI Control Center guide
│       ├── AudioSync/            # AUDIOSYNC guide
│       ├── VNA/                  # Visual Network Analyzer guide
│       └── ConnectionToolkit/    # Connection Toolkit guide
│
├── MADTOM_GOLANG/                # Go Backend Services
│   ├── cmd/                      # Daemon & collector main entrypoints
│   ├── pkg/                      # TSDB, downsampling, scrapers, transports
│   ├── systemd/                  # Unit files with flag documentation
│   ├── build.sh                  # Multi-architecture Go compiler
│   └── deploy.sh                 # Remote SSH deployment script
│
└── MADTOM_DOTNET/                # .NET C# Avalonia Solution
    ├── MADTOM.sln / MADTOM.slnx  # Visual Studio / .NET Solution files
    ├── publish.sh                # Linux self-contained publishing script
    ├── src/
    │   ├── Core/                 # Plugin contracts & interfaces (MADTOM.PluginContracts)
    │   ├── Host/MADTOM.Studio/   # Main Avalonia desktop UI executable & tray host
    │   └── Plugins/              # Modular plugins & standalone runners
    │       ├── Telemetry/        # Telemetry views, charts, and standalone app
    │       ├── Squeeze/          # SQUEEZE transcode engine, views, and app
    │       ├── AndroidToolkit/   # Android toolkit views and standalone app
    │       ├── MediaCenter/      # MediaCenter views and standalone app
    │       ├── NoxAI/            # NOX AI control center views and standalone app
    │       ├── AudioSync/        # AUDIOSYNC views and standalone app
    │       ├── VNA/              # Visual Network Analyzer views and standalone app
    │       └── ConnectionToolkit/# Connection Toolkit views and standalone app
    └── MadTOM.Tests/             # Decoupled targeted test suites
        ├── Studio/               # Host settings, theming, lexicon, tray menu tests
        └── Plugins/              # Targeted per-plugin test projects
            ├── Telemetry/        # Telemetry cache, LTTB, Zstd, metrics tests
            ├── Squeeze/          # Transcode queue, preset, stager, transfer tests
            ├── AndroidToolkit/   # Lifecycle & ADB model tests
            ├── MediaCenter/      # Lifecycle & streaming model tests
            ├── NoxAI/            # Lifecycle & LLM inference model tests
            ├── AudioSync/        # Lifecycle & PTP clock sync tests
            ├── VNA/              # Lifecycle & network model tests
            └── ConnectionToolkit/# Lifecycle & proxy model tests
```
