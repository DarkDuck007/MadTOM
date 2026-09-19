# Command-Line & Scripts Reference

This guide provides an exhaustive reference for all executables, build scripts, deployment tools, and publishing utilities in the MADTOM project.

---

## Table of Contents

1. [Scripts Reference](#scripts-reference)
   - [`./build.sh` — Unified Project Build](#1-buildsh--unified-project-build)
   - [`./publish.sh` — .NET Self-Contained Linux Publishing](#2-publishsh--net-self-contained-linux-publishing)
   - [`./deploy.sh` — Remote SSH/Sudo Daemon Deployment](#3-deploysh--remote-sshsudo-daemon-deployment)
   - [`MADTOM_GOLANG/build.sh` — Go Multi-Architecture Compiler](#4-madtom_golangbuildsh--go-multi-architecture-compiler)
2. [Executables Reference](#executables-reference)
   - [`madtom-daemon` — Node Telemetry Agent](#1-madtom-daemon--node-telemetry-agent)
   - [`madtom-collector` — Central Telemetry Hub](#2-madtom-collector--central-telemetry-hub)
   - [`MADTOM.Console` — Avalonia Desktop Application](#3-madtomconsole--avalonia-desktop-application)
   - [`MADTOM.Plugins.Telemetry.App` — Standalone Telemetry Client](#4-madtompluginstelemetryapp--standalone-telemetry-client)

---

## Scripts Reference

### 1. `./build.sh` — Unified Project Build

Located at the repository root. Compiles the Go backend daemons (`madtom-collector`, `madtom-daemon`) and builds the C# .NET solution (`MADTOM.sln`).

#### Syntax
```bash
./build.sh [OPTIONS]
```

#### Options
| Option | Argument | Description |
|---|---|---|
| `-a`, `--arch` | `amd64` \| `arm64` \| `arm` \| `all` | Target architecture for Go backend (default: host architecture) |
| `--release` | *(none)* | Build Go and .NET with release optimizations (strips Go debug symbols) |
| `--debug` | *(none)* | Build projects in Debug configuration (default) |
| `--test` | *(none)* | Run Go tests (`go test ./...`) and .NET test suite (`dotnet test`) |
| `--clean` | *(none)* | Remove previous build artifacts prior to compiling |
| `-h`, `--help` | *(none)* | Display help message and exit |

#### Examples
```bash
# Standard debug build of all projects
./build.sh

# Build release binaries and run full automated test suites
./build.sh --release --test

# Build Go daemons specifically for ARM64 and build .NET solution
./build.sh --arch arm64

# Clean previous outputs and build for all Go architectures
./build.sh --clean --arch all --release
```

---

### 2. `./publish.sh` — .NET Self-Contained Linux Publishing

Located at `MADTOM_DOTNET/publish.sh` and symlinked to root `./publish.sh`. Produces self-contained, single-file Linux executables with embedded native SkiaSharp and Avalonia runtime libraries. Target machines do **not** need the .NET SDK or runtime installed.

**Output directory**: `MADTOM_DOTNET/publish/{OS_arch}/`

#### Syntax
```bash
./publish.sh [OPTIONS]
```

#### Options
| Option | Argument | Description |
|---|---|---|
| `-a`, `--arch`, `-r`, `--rid` | `linux-x64` \| `linux-arm64` \| `linux-arm` \| `all` | Target Linux RID/architecture (default: host RID). Aliases: `x64`, `amd64`, `arm64`, `aarch64`, `arm`, `armv7` |
| `-p`, `--project` | `console` \| `telemetry-app` \| `all` | Project to publish (default: `all`) |
| `-c`, `--config` | `Release` \| `Debug` | Build configuration (default: `Release`) |
| `--single-file` | *(none)* | Package into single-file executable (default: enabled) |
| `--no-single-file` | *(none)* | Output loose directory of assemblies and native shared libraries |
| `--clean` | *(none)* | Delete `MADTOM_DOTNET/publish/` before building |
| `-h`, `--help` | *(none)* | Display help message and exit |

#### Examples
```bash
# Publish all projects for host architecture
./publish.sh

# Cross-publish for 64-bit ARM Linux (e.g. Raspberry Pi 4/5, Oracle ARM, AWS Graviton)
./publish.sh --arch arm64

# Cross-publish for all supported Linux architectures (x64, arm64, armv7)
./publish.sh --arch all

# Publish only the main desktop console application for ARM64
./publish.sh -p console -a linux-arm64
```

---

### 3. `./deploy.sh` — Remote SSH/Sudo Daemon Deployment

Located at `MADTOM_GOLANG/deploy.sh` and symlinked to root `./deploy.sh`. Deploys Go daemons to a remote Linux host via SSH, staging in `/tmp`, copying into the destination with `sudo`, and restarting the remote systemd service.

#### Key Features:
- **Multi-Host YAML Deployment**: Deploy to multiple servers sequentially with a single command via `-c / --config deploy.yaml`.
- **Non-Interactive Authentication**: Pass SSH and sudo passwords directly via CLI flags (`--ssh-pass`, `--sudo-pass`) or YAML configuration for automation.
- **Custom Service Overrides**: Override systemd service unit name per server or globally via `-s / --service`.
- **Architecture Auto-Detection & Build Caching**: When `--arch auto` (default) is used, queries `uname -m` over SSH and compiles for the remote architecture automatically, caching builds across identical target architectures.
- **Binary Architecture Validation**: Uses `file -b` to verify the binary matches the destination architecture before uploading, preventing remote `Exec format error`.
- **Safe Sudo Staging**: Passes the sudo password securely via standard input without exposing it in process listings.
- **Systemd Alignment**: Inspects the remote service's `ExecStart` path and automatically syncs the binary to that location.

#### Syntax
```bash
./deploy.sh [OPTIONS] [USER@HOST | USER HOST]
./deploy.sh -c config.yaml [OPTIONS]
```

#### Options
| Option | Argument | Default | Description |
|---|---|---|---|
| `-c`, `--config` | `PATH` | *(none)* | Path to YAML configuration file for multi-host deployment |
| `-u`, `--user` | `USER` | Interactive prompt | Remote SSH username |
| `-h`, `--host` | `HOST` | Interactive prompt | Remote hostname or IP address |
| `-p`, `--port` | `PORT` | `22` | SSH port |
| `-b`, `--binary` | `PATH` | `bin/madtom-daemon` | Local executable to deploy |
| `-d`, `--dest` | `PATH` | `/opt/madtomd` | Remote target destination path |
| `-s`, `--service` | `NAME` | `madtomd.service` | Remote systemd service name to restart |
| `-a`, `--arch` | `amd64` \| `arm64` \| `arm` \| `auto` | `auto` | Target architecture (auto-probed via SSH) |
| `--ssh-pass` | `PASS` | *(none)* | Remote SSH login password |
| `--sudo-pass` | `PASS` | `--ssh-pass` | Remote sudo elevation password |
| `--build` | *(none)* | Disabled | Force local Go compilation before deployment |
| `--dry-run` | *(none)* | Disabled | Validate deployment configuration without connecting |
| `--help` | *(none)* | *(none)* | Show usage help and exit |

#### Examples
```bash
# Multi-host deployment from YAML config
./deploy.sh -c deploy.yaml

# Deploy to multiple hosts overriding the restarted systemd service name
./deploy.sh -c deploy.yaml -s madtomd.service

# Non-interactive single-host deployment with CLI passwords
./deploy.sh -u danial -h la.realiteam.art --ssh-pass secret123 --sudo-pass secret123

# Interactive deployment (prompts for user, host, and password)
./deploy.sh

# Deploy to an ARM64 server, forcing compilation
./deploy.sh -u danial -h 10.0.0.12 -a arm64 --build

# Deploy the collector hub instead of the node daemon
./deploy.sh -u admin -h 10.0.0.15 \
  -b bin/madtom-collector \
  -d /opt/madtom-collector \
  -s madtom-collector.service
```

---

### 4. `MADTOM_GOLANG/build.sh` — Go Multi-Architecture Compiler

Dedicated compilation script for Go backend binaries (`madtom-daemon` and `madtom-collector`).

**Output directory**: `MADTOM_GOLANG/bin/` and `MADTOM_GOLANG/bin/linux_{arch}/`

#### Syntax
```bash
./MADTOM_GOLANG/build.sh [OPTIONS]
```

#### Options
| Option | Argument | Default | Description |
|---|---|---|---|
| `-a`, `--arch` | `amd64` \| `arm64` \| `arm` \| `all` | Host arch | Architecture to compile for |
| `-p`, `--package` | `daemon` \| `collector` \| `all` | `all` | Package(s) to compile |
| `-c`, `--clean` | *(none)* | Disabled | Clean `bin/` directory prior to compiling |
| `--release` | *(none)* | Enabled | Strip debug symbols (`-ldflags="-s -w"`) |
| `--debug` | *(none)* | Disabled | Retain debug symbols |
| `-h`, `--help` | *(none)* | *(none)* | Display help message and exit |

#### Examples
```bash
# Compile both daemons for host architecture
./MADTOM_GOLANG/build.sh

# Compile both daemons for ARM64
./MADTOM_GOLANG/build.sh --arch arm64

# Compile only madtom-daemon for 32-bit ARM (ARMv7)
./MADTOM_GOLANG/build.sh -p daemon -a arm

# Compile both daemons across all architectures (amd64, arm64, arm)
./MADTOM_GOLANG/build.sh --arch all
```

---

## Executables Reference

### 1. `madtom-daemon` — Node Telemetry Agent

The daemon runs on each monitored Linux host. It samples CPU, memory, network interfaces, block I/O, processes, and optional TWAMP latency probes.

#### Command Arguments
| Argument | Type | Default | Description |
|---|---|---|---|
| `-node-id` | `string` | System hostname | Unique identifier for this monitored node reported in the UI and TSDB |
| `-mode` | `string` | `push` | Ingestion mode: `push`, `pull`, or `reverse-push` |
| `-collector` | `host:port` | `127.0.0.1:50051` | Collector gRPC address (used in `push` mode) |
| `-listen-port` | `int` | `50052` | Port daemon listens on (used in `pull` and `reverse-push` modes) |
| `-spool-dir` | `path` | `/tmp/madtom/wal` | Local WAL directory containing grouped segment files and durable `wal-state.json` replay cursors |
| `-max-spool-mb` | `int64` | `1024` (1 GB) | Maximum disk space for WAL spool before circular segment eviction |
| `--migrate` | `bool` | `false` | Run pending, versioned WAL migrations on startup, before collection or transport, then continue normal operation. Failure exits startup. |
| `-zstd` | `bool` | `false` | Enable zstd compression for on-disk WAL segments and gRPC batches |
| `-twamp-target` | `string` | `""` | Target TWAMP Light UDP reflector (`collector`, `auto`, bare IP/host, or `host:port`) |
| `-twamp-port` | `int` | `862` | Default UDP port for TWAMP Light probing when target lacks an explicit port |
| `-twamp-clocks-synchronized` | `bool` | `false` | Assert synchronized clocks (NTP/PTP) to enable true one-way latency |

To migrate an existing spool, stop its daemon and restart with the same settings plus `--migrate`:

```bash
./madtom-daemon --migrate -spool-dir=/var/lib/madtomd/wal -node-id=my-node
```

Include your usual mode, collector, quota and compression flags. Successful migration continues into normal daemon operation; repeated use skips completed versions. Without `--migrate`, startup never runs the migration suite. An interrupted migration blocks normal startup and instructs you to restart with `--migrate`. The daemon holds an exclusive spool-directory lock; older binaries do not honor this lock, so stop them first.

Version 0 means an unversioned legacy spool. Version 1 repacks pending records into replay-sized batches, preserving sample order and acknowledged prefixes. Migration temporarily needs space for both the original spool and its replacements; staging does not evict data to meet `-max-spool-mb`. Replacement encoding follows `-zstd`, so converting compressed originals to raw may require substantially more space. Normal quota eviction resumes after startup.

A single sample exceeding the replay limit, corrupt/incomplete records, or data beyond the 64 MiB record/decode safety bounds stops migration with an error. Originals remain intact before cutover; oversized individual samples still need a future transport-format solution. This command does not migrate Collector databases or UI caches. Newer unsupported WAL versions are rejected; downgrade compatibility is not promised.


#### Execution Examples
```bash
# Push Mode (Daemon connects to central collector)
./madtom-daemon \
  -node-id="web-node-01" \
  -mode="push" \
  -collector="collector.internal:50051" \
  -spool-dir="/var/lib/madtomd/wal" \
  -max-spool-mb=2048

# Pull Mode (Daemon listens on port 50052; collector polls it)
./madtom-daemon \
  -node-id="db-node-01" \
  -mode="pull" \
  -listen-port=50052 \
  -spool-dir="/var/lib/madtomd/wal"

# Reverse-Push Mode (Daemon listens; collector connects, daemon streams back)
./madtom-daemon \
  -node-id="edge-node-01" \
  -mode="reverse-push" \
  -listen-port=50052

# Enabling TWAMP Light Latency Probes against the Collector
./madtom-daemon \
  -node-id="edge-node-01" \
  -mode="push" \
  -collector="collector.internal:50051" \
  -twamp-target="collector" \
  -twamp-clocks-synchronized=true

# Enabling TWAMP Probes against a custom gateway port
./madtom-daemon \
  -node-id="edge-node-02" \
  -mode="push" \
  -collector="collector.internal:50051" \
  -twamp-target="gateway.internal:8620"
```

---

### 2. `madtom-collector` — Central Telemetry Hub

The collector aggregates telemetry from all daemons, persists metrics into an embedded CockroachDB Pebble TSDB, manages live subscriptions, provides a native TWAMP Light UDP reflector, and serves operator UI queries via gRPC.

#### Command Arguments
| Argument | Type | Default | Description |
|---|---|---|---|
| `-name` | `string` | `"Local Collector"` | Display name shown on UI node cards |
| `-port` | `int` | `50051` | Unified gRPC TCP port (serves ingestion, live streaming, and UI queries) |
| `-twamp-port` | `int` | `862` | UDP port for native RFC 5357 TWAMP Light reflector (`0` disables). Note: port 862 requires `CAP_NET_BIND_SERVICE` or root. |
| `-data-dir` | `path` | `/tmp/madtom/collector_data` | Pebble TSDB directory path for persistent time-series data |
| `-pull-targets` | `string` | `""` | Comma-separated list of pull daemons: `node-id@host:port,...` |
| `-pull-interval` | `duration` | `1s` | Polling frequency for pull targets (default 1s for 1Hz resolution) |
| `-reverse-push-targets` | `string` | `""` | Comma-separated list of reverse-push targets: `node-id@host:port,...` |

#### Execution Examples
```bash
# Basic standalone collector with native TWAMP reflector
./madtom-collector \
  -name="Lab Gateway" \
  -port=50051 \
  -twamp-port=862 \
  -data-dir="/var/lib/madtom-collector/data"

# Standalone collector with unprivileged TWAMP port
./madtom-collector \
  -name="Lab Gateway" \
  -port=50051 \
  -twamp-port=8620 \
  -data-dir="/var/lib/madtom-collector/data"

# Basic standalone collector with standard port 862
./madtom-collector \
  -name="Primary Hub" \
  -port=50051 \
  -twamp-port=862 \
  -data-dir="/var/lib/madtom/collector_data"

# Collector polling pull-mode nodes across subnets
./madtom-collector \
  -name="Hub-East" \
  -port=50051 \
  -data-dir="/var/lib/madtom/collector_data" \
  -pull-targets="Node1@10.0.1.10:50052,Node2@10.0.1.11:50052" \
  -pull-interval=1s

# Collector connecting to reverse-push daemons behind NATs/firewalls
./madtom-collector \
  -name="Cloud Hub" \
  -port=50051 \
  -data-dir="/var/lib/madtom/collector_data" \
  -reverse-push-targets="Sakura1@realiteam.art:50052,La1@la.realiteam.art:50052"
```

---

### 3. `MADTOM.Console` — Avalonia Desktop Application

The primary desktop user interface for monitoring nodes, fleet topology, downsampled graphs, and process lists.

- **Technology**: Avalonia UI (.NET 10)
- **Executable**: `MADTOM_DOTNET/publish/{OS_arch}/MADTOM.Console/MADTOM.Console`

#### Running
```bash
# Direct execution (self-contained executable)
./MADTOM_DOTNET/publish/linux-x64/MADTOM.Console/MADTOM.Console

# Or running from source
dotnet run --project MADTOM_DOTNET/src/Host/MADTOM.Console/MADTOM.Console.csproj
```

---

### 4. `MADTOM.Plugins.Telemetry.App` — Standalone Telemetry Client

A standalone, focused telemetry viewer packaging the telemetry plugin directly.

- **Technology**: Avalonia UI (.NET 10)
- **Executable**: `MADTOM_DOTNET/publish/{OS_arch}/MADTOM.Plugins.Telemetry.App/MADTOM.Plugins.Telemetry.App`

#### Running
```bash
./MADTOM_DOTNET/publish/linux-x64/MADTOM.Plugins.Telemetry.App/MADTOM.Plugins.Telemetry.App
```

