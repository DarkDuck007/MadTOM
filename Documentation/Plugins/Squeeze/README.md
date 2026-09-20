# SQUEEZE

> High-performance distributed media transcoding engine combining a lightweight Go FFmpeg daemon with a responsive cross-platform Avalonia client (Desktop & Android).

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET Version](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Go Version](https://img.shields.io/badge/Go-1.22+-00ADD8.svg)](https://go.dev/)
[![Avalonia](https://img.shields.io/badge/Avalonia-11.2-1182c4.svg)](https://avaloniaui.net/)

---

## Highlights

- **Hardware-Probed Transcoding**: Automatically verifies hardware encoder capabilities (VA-API, NVENC, QSV, AMF) via cached single-frame dry-runs.
- **Zero-Conf Multi-Vector Discovery**: Seamlessly locates transcoding servers across local LANs and Wi-Fi hotspots via mDNS multicast, directed subnet UDP broadcast, and HTTP sweeps.
- **Robust Transfer Pipeline**: Streamed resumable chunked uploads, SSE real-time telemetry, and safe streamed downloads.
- **Adaptive Cross-Platform UI**: Responsive Avalonia MVVM application optimized for both multi-monitor desktops and narrow touch-screen mobile devices.

---

## Quick Start

### 1. Start the Transcoding Daemon (`SQUEEZE_SERVER`)

**Prerequisites**: Go 1.22+, FFmpeg, and FFprobe.

```sh
# Navigate to server directory
cd SQUEEZE_SERVER

# Compile server binary
go build -o bin/squeeze-server ./cmd/server

# Start daemon with sample configuration
./bin/squeeze-server --config config.sample.yaml
```

*By default, the server begins listening on `http://0.0.0.0:8080` and advertises itself over mDNS.*

### 2. Launch the Desktop Client (`SQUEEZE_UI`)

**Prerequisites**: .NET 10 SDK.

```sh
# Run Avalonia Desktop UI
dotnet run --project SQUEEZE_UI/SQUEEZE.Desktop/SQUEEZE.Desktop.csproj
```

### 3. Deploy to Android Device

**Prerequisites**: Android SDK / ADB.

```sh
# Package standalone APK with embedded assemblies
dotnet build SQUEEZE_UI/SQUEEZE.Android/SQUEEZE.Android.csproj -t:PackageForAndroid

# Install to connected device
adb install -r SQUEEZE_UI/SQUEEZE.Android/bin/Debug/net10.0-android/com.squeeze.transcoder-Signed.apk

# Launch app
adb shell am start -n com.squeeze.transcoder/crc6420f27725dc9c1856.MainActivity
```

---

## Documentation Directory

For in-depth technical manuals, architectural diagrams, CLI references, and protocol specifications, refer to the guides in this directory:

- **[System Architecture](ARCHITECTURE.md)**: Network topology, multi-vector discovery sequence, job state machine, and reactive UI architecture.
- **[Server Reference Manual](SERVER_REFERENCE.md)**: Complete CLI flags table, YAML configuration schema, hardware probing mechanics, and retention policies.
- **[Client User & Developer Guide](CLIENT_GUIDE.md)**: Desktop and Android client usage, responsive UI layouts, touch optimizations, and APK packaging.
- **[Protocol Specification](PROTOCOL_SPEC.md)**: REST API contracts, Server-Sent Events (SSE) specifications, and mDNS service schemas.

---

## Verification & Testing

To run the automated formatting and xUnit test suites:

```sh
python3 .agents/skills/squeeze-test-runner/scripts/test_runner.py run
```

To run end-to-end smoke testing with an isolated loopback daemon:

```sh
python3 scripts/smoke_test.py
```

To verify documentation integrity and check for configuration drift:

```sh
python3 .agents/skills/squeeze-doc-sync/scripts/doc_sync.py check
```

