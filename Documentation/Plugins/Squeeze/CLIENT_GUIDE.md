# SQUEEZE Client User & Developer Guide

This guide describes the architecture, responsive interface features, workflow operations, and deployment procedures for the Avalonia C# client (`SQUEEZE_UI`).

---

## 1. Overview & Solution Structure

The client is built using Avalonia UI and the MVVM design pattern targeting .NET 10:

```
SQUEEZE_UI/
├── Directory.Build.props       # Avalonia version & build properties
├── SQUEEZE/                    # Core multiplatform library (Views, ViewModels, Services, Models)
├── SQUEEZE.Desktop/            # Desktop entry point (Linux, macOS, Windows)
└── SQUEEZE.Android/            # Android native host project (API 26+)
```

---

## 2. Desktop Quick Start

### Build & Run Desktop

From repository root:

```sh
dotnet run --project SQUEEZE_UI/SQUEEZE.Desktop/SQUEEZE.Desktop.csproj
```

### Build Release Artifacts

```sh
dotnet publish SQUEEZE_UI/SQUEEZE.Desktop/SQUEEZE.Desktop.csproj -c Release -r linux-x64 --self-contained
```

---

## 3. Mobile / Android Deployment Guide

### Standalone APK Compilation
To install the APK on an Android device via `adb` or standard package installer without requiring Visual Studio Fast Deployment, the project embeds all .NET assemblies:

In `SQUEEZE_UI/SQUEEZE.Android/SQUEEZE.Android.csproj`:
```xml
<EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>
<AndroidUseFastDeployment>false</AndroidUseFastDeployment>
```

### Build & Package APK

```sh
dotnet build SQUEEZE_UI/SQUEEZE.Android/SQUEEZE.Android.csproj -t:PackageForAndroid
```

Signed APK output path:
`SQUEEZE_UI/SQUEEZE.Android/bin/Debug/net10.0-android/com.squeeze.transcoder-Signed.apk`

### Install and Launch via ADB

```sh
# Install or upgrade on connected device
adb install -r SQUEEZE_UI/SQUEEZE.Android/bin/Debug/net10.0-android/com.squeeze.transcoder-Signed.apk

# Launch MainActivity
adb shell am start -n com.squeeze.transcoder/crc6420f27725dc9c1856.MainActivity
```

---

## 4. UI Architecture & Responsive Features

### Responsive Layout & Breakpoints
The UI automatically adapts across form factors (from 4K desktop monitors to narrow mobile screens):
- **Collapsible Batch Job Queue**: The left panel can be collapsed via a toggle button, granting full screen focus to the active encode deck on mobile screens.
- **Server Discovery Modal**: The Connection & Discovery dialog is sized dynamically with bounded dimensions and internal scrolling to remain fully visible on narrow mobile displays.
- **Touch-Optimized Scrollbars**: Scrollbar widths and margins are adjusted to prevent obscuring header buttons and navigation controls.

### Streamlined Transcoding Workflow
- **Browse File**: Opens system file picker (`*/*`, `video/*`) and stages media into local app cache on mobile.
- **Unified `[▶ START]` Button**: Single action initiates file upload and automatically queues the transcode on the remote server.
- **Per-Job Controls**: In the batch queue, each item provides individual **Pause** and **Cancel** buttons.
- **Preset Catalog**: Categorized into *General*, *Web*, *Hardware*, and *Production*. The currently active preset title is clearly displayed and rotatable.
- **Expandable FFmpeg Preview**: The generated FFmpeg command line preview is multiline and expandable, allowing users to inspect exact encoder flags before processing.

---

## 5. Network & Discovery Mechanics

The client integrates `MdnsServerDiscoveryService`:
- Discovers LAN instances of `_squeeze._tcp.local` via multicast DNS (`224.0.0.251:5353`).
- Sends directed subnet UDP broadcasts (e.g. `192.168.43.255:5353`) to discover servers even on mobile hotspots where standard multicast is suppressed.
- Filters out non-RFC 1918 cellular and dummy gateway addresses (`192.0.0.1`).
- Runs an HTTP health handshake against candidate nodes before exposing them in the UI.

