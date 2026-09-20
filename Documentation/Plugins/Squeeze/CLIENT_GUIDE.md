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

The maintained MADTOM plugin lives in `MADTOM_DOTNET/src/Plugins/Squeeze/`. See its [UI guide](../../../MADTOM_DOTNET/src/Plugins/Squeeze/README.md) for current build commands and verification instructions. The earlier standalone `SQUEEZE_UI` paths above describe the legacy client.

### Responsive Layout & Breakpoints

- **Desktop (760 logical pixels and wider)**: A resizable, collapsible batch queue sits beside the encoding editor. Sidebar width and collapse state survive switching to a narrow viewport and back.
- **Mobile / narrow windows**: **Encode settings** and the job-count button switch between full-width panes. Toolbar actions and parameter tabs wrap, form labels stack above inputs, and controls have a minimum 40-pixel height; desktop buttons use compact 30-pixel minimum heights with centered icons and labels.
- **Dialogs**: Settings, hardware, preset catalog, and save-preset dialogs all support desktop edge/corner resizing with visible grips, bounded dimensions, and scrolling. Escape closes the active dialog and restores keyboard focus. Background workspace controls are disabled while a dialog is open.

### Preset Catalog

- Search names, descriptions, codecs, containers, resolution labels, categories, and tags. Search is case-insensitive; every space-separated word must match somewhere in a preset.
- The catalog displays separate expandable groups, initially collapsed. Filtering expands matching groups; clearing filters restores the previous expansion choices. Filter by category, including **Custom** and categories received from the server. **Reset** clears the query and category; an empty-results message explains how to recover.
- On desktop, drag any edge or corner to resize the centered catalog, following Telemetry's dialog interaction. The catalog stays within the plugin viewport and remembers its size for the lifetime of the view. **Reset size** restores its initial dimensions.
- On narrow screens, the catalog fills the available viewport; grouped lists scroll internally. Opening the catalog focuses search.
- **Save preset** captures current settings as a named user profile in a selected group. User profiles survive server catalog refreshes regardless of their group. **Set default** uses a saved profile as the startup default; modified settings must be saved first. Existing persistence semantics remain unchanged.

### Transcoding Workflow

1. Open **Settings** and connect to a server or discover a LAN node.
2. Click or tap the **source media bar** to choose a video, or drop a media file onto the workspace. Mobile storage selections retain stream staging support.
3. Choose a preset and adjust parameters, then select **Start encode** to upload and enqueue.
4. Use per-job pause, resume, cancel, and download actions in the queue.

The expandable, read-only FFmpeg preview remains available. The quality slider correctly labels higher quality at the low-value end and smaller files at the high-value end. Dynamic MADTOM theme brushes continue to update existing views without recreating the plugin.

---

## 5. Network & Discovery Mechanics

The client integrates `MdnsServerDiscoveryService`:
- Discovers LAN instances of `_squeeze._tcp.local` via multicast DNS (`224.0.0.251:5353`).
- Sends directed subnet UDP broadcasts (e.g. `192.168.43.255:5353`) to discover servers even on mobile hotspots where standard multicast is suppressed.
- Filters out non-RFC 1918 cellular and dummy gateway addresses (`192.0.0.1`).
- Runs an HTTP health handshake against candidate nodes before exposing them in the UI.

