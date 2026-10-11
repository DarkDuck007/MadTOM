# MADTOM Studio: Plugin Architecture Guide

This document defines the architecture, service contracts, and lifecycle patterns for building and hosting plugins in **MADTOM Studio**.

---

## Table of Contents

- [Architectural Overview](#architectural-overview)
- [The Plugin Contract Layer (`MADTOM.PluginContracts`)](#the-plugin-contract-layer-madtomplugincontracts)
  - [`IPluginModule`](#ipluginmodule)
  - [`IPluginHostContext`](#ipluginhostcontext)
- [Sidebar Navigation & Plugin Ordering](#sidebar-navigation--plugin-ordering)
  - [Order Weight Hierarchy](#order-weight-hierarchy)
  - [Placeholder Dimming & Status Indicator](#placeholder-dimming--status-indicator)
- [Reusable Shared UI Controls (`MADTOM.PluginContracts/Controls`)](#reusable-shared-ui-controls-madtomplugincontractscontrols)
  - [`UniversalModalControl`](#universalmodalcontrol)
  - [`VideoFeedViewerControl`](#videofeedviewercontrol)
  - [`CenteredDialogResizer`](#centereddialogresizer)
- [Host Services Available to Plugins](#host-services-available-to-plugins)
  - [System Tray Integration (`ITrayMenuService`)](#system-tray-integration-itraymenuservice)
  - [Dynamic Theming (`IThemeHost`)](#dynamic-theming-ithemehost)
  - [Lexicon & Localization (`ILexiconHost`)](#lexicon--localization-ilexiconhost)
  - [Desktop Notifications (`IPluginNotificationService`)](#desktop-notifications-ipluginnotificationservice)
- [Plugin Lifecycle & Execution Flow](#plugin-lifecycle--execution-flow)
- [Step-by-Step: Creating a New Plugin](#step-by-step-creating-a-new-plugin)
  - [1. Project Setup](#1-project-setup)
  - [2. Implement `IPluginModule`](#2-implement-ipluginmodule)
  - [3. Register Custom System Tray Section](#3-register-custom-system-tray-section)
  - [4. Add to Solution & Host](#4-add-to-solution--host)

---

## Architectural Overview

MADTOM Studio is designed as a modular host application. The core studio shell provides the desktop window, layout scaling, application-level system tray integration, theme management, and lexicon dialect switching.

Specific features (such as distributed telemetry, media transcoding, video feed aggregation, camera streaming, or network diagnostics) live entirely within separate, decoupled plugin assemblies.

```mermaid
graph TD
    subgraph Host Application
        C["MADTOM.Studio<br/>(Avalonia Desktop Shell)"]
        TM["TrayHost & TrayMenuService"]
        TH["ThemeManager"]
        LH["LexiconService"]
    end

    subgraph Contract Layer
        PC["MADTOM.PluginContracts<br/>(Shared Interfaces, Controls & Abstractions)"]
    end

    subgraph Core Active Plugins
        P1["MADTOM.Plugins.Telemetry<br/>(Telemetry & TSDB Visualization)"]
        P2["MADTOM.Plugins.Squeeze<br/>(Media Transcoding Toolkit)"]
        P3["MADTOM.Plugins.VideoFeedCenter<br/>(Multi-Channel Video Grid & Ingestion)"]
        P4["MADTOM.Plugins.IPCam<br/>(Mobile & On-Device Camera Streamer)"]
    end

    C --> PC
    P1 --> PC
    P2 --> PC
    P3 --> PC
    P4 --> PC
    C -.->|"Loads via IPluginModule"| P1
    C -.->|"Loads via IPluginModule"| P2
    C -.->|"Loads via IPluginModule"| P3
    C -.->|"Loads via IPluginModule"| P4
    P1 -->|"Registers Section"| TM
    P2 -->|"Registers Section"| TM
    TH -.->|"ThemeChanged Event"| P1
    TH -.->|"ThemeChanged Event"| P2
```

---

## The Plugin Contract Layer (`MADTOM.PluginContracts`)

The `MADTOM.PluginContracts` assembly contains lightweight, zero-dependency interfaces that define the communication boundary between the host studio and plugins.

### `IPluginModule`

Every plugin entry point implements `IPluginModule`:

```csharp
namespace MADTOM.PluginContracts;

public interface IPluginModule : IPluginLifecycle
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    string IconGlyph { get; }
    string Category { get; }
    Version Version { get; }
    int OrderWeight { get; }

    Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default);
    Control CreateView();
}
```

### `IPluginHostContext`

During initialization, the host passes an `IPluginHostContext` instance to the plugin:

```csharp
namespace MADTOM.PluginContracts;

public interface IPluginHostContext
{
    IThemeHost Theme { get; }
    ILexiconHost Lexicon { get; }
    ITrayMenuService? Tray { get; }
    IPluginNotificationService Notifications { get; }
    string StorageDirectory { get; }
}
```

- **`Theme`**: Provides current theme palette resources and raises events when the user changes themes.
- **`Lexicon`**: Resolves localized dialect strings for UI text.
- **`Tray`**: Allows the plugin to add dynamic items to the studio's system tray menu (null in headless or standalone runner contexts).
- **`Notifications`**: Dispatches toast and system notifications to the operator.
- **`StorageDirectory`**: Safe directory (`~/.local/share/MADTOM/plugins/{PluginId}/`) for isolated plugin persistence.

---

## Sidebar Navigation & Plugin Ordering

The studio's navigation rail orders and presents plugins according to their metadata and operational readiness.

### Order Weight Hierarchy

The `OrderWeight` property determines the vertical ordering of plugin icons in the sidebar navigation rail (ascending, lower weights appear first):

| Order Weight | Module ID | Display Name | Status |
|---|---|---|---|
| `10` | `telemetry` | Telemetry & TSDB | **Active / Running** |
| `20` | `squeeze` | SQUEEZE Media Transcoding | **Active / Running** |
| `30` | `videofeedcenter` | Video Feed Center | **Active / Running** |
| `40` | `ipcam` | IPCam Management | **Active / Running** |
| `100` | `android-toolkit` | Android Toolkit | Stub / Coming Soon |
| `110` | `media-center` | Media Center | Stub / Coming Soon |
| `120` | `nox-ai` | Nox AI | Stub / Coming Soon |
| `130` | `audio-sync` | Audio Sync | Stub / Coming Soon |
| `140` | `vna` | Visual Network Analyzer | Stub / Coming Soon |
| `150` | `connection-toolkit` | Connection Toolkit | Stub / Coming Soon |
| `900` | `sysadmin` | System Administration | Stub / Coming Soon |
| `910` | `esc` | ESC Diagnostics | Stub / Coming Soon |

### Placeholder Dimming & Status Indicator

To clearly differentiate between functional modules and future capabilities:
- **Implemented Plugins** (`telemetry`, `squeeze`, `videofeedcenter`, `ipcam`): Display with full opacity (`1.0`), clickable interactive navigation, and a green status pill (`• RUNNING`).
- **Placeholder / Coming Soon Plugins**: Render with dimmed opacity (`0.4`), subdued cursor interaction, and an amber indicator pill (`• COMING SOON`).

---

## Reusable Shared UI Controls (`MADTOM.PluginContracts/Controls`)

To prevent code duplication across plugins and enforce consistent UX and theme conformance, reusable UI components are housed in `MADTOM.PluginContracts/Controls`.

### `UniversalModalControl`

A flexible popup/modal control that renders above main views with consistent styling:
- **Backdrop Scrim**: Semi-transparent darkened background with click-outside dismiss handling.
- **Header & Action Bar**: Standard title bar with close icon button (`✕`), customizable actions, and keyboard shortcuts (`Escape` to dismiss).
- **Auto-Centering & Resizing**: Integrates with `CenteredDialogResizer` to ensure comfortable viewing on touchscreens and small resolutions.
- **Theme Binding**: Automatically uses host dynamic resources (`Background`, `SurfaceElevated`, `TextPrimary`, `BorderSubtle`).

### `VideoFeedViewerControl`

A high-performance video feed display canvas used by `VideoFeedCenter` and `IPCam`:
- **Direct Frame Buffer Rendering**: Efficiently renders RGB24 and RGBA raw frame buffers using Avalonia `WriteableBitmap`.
- **Aspect Ratio Preservation**: Automatically centers and letterboxes video streams without distorting aspect ratios during window resizes.
- **HUD Diagnostics Overlay**: Semi-transparent, toggleable overlay displaying live FPS, stream latency, resolution, and connection status.
- **Interaction Controls**: Click-to-maximize, full-screen expansion, and Picture-in-Picture (PiP) triggers.

### `CenteredDialogResizer`

A layout helper utility ensuring dialogs, alerts, and settings windows maintain proportional dimensions relative to the parent window while enforcing minimum touch target sizes (`44x44px`) and safe margin bounds.

---

## Host Services Available to Plugins

### System Tray Integration (`ITrayMenuService`)

Plugins should never instantiate their own OS tray icons when running inside MADTOM Studio. Instead, they register dynamic sections with the host's tray menu:

```csharp
if (context.Tray != null)
{
    var mySection = new CustomTrayMenuSection("My Plugin");
    context.Tray.AddSection(mySection);
}
```

- **Automatic Section Headers**: The studio automatically inserts a compact, non-intrusive header (`— {PluginName} —`) rendered in native muted color above the plugin's items.
- **Standalone Mode Resilience**: Standalone runner applications (such as `MADTOM.Plugins.Telemetry.App`) can provide a null or dummy `ITrayMenuService` without breaking plugin code.

### Dynamic Theming (`IThemeHost`)

Plugins inherit the studio's active color palette:
- Plugin controls subscribe to theme resource keys (e.g., `DynamicResource Background`, `DynamicResource SurfaceElevated`).
- Plugins can subscribe to `context.Theme.ThemeChanged` to update non-XAML canvas drawing brushes or custom render loops.

### Lexicon & Localization (`ILexiconHost`)

Plugins register custom terms with the host lexicon system or query existing dialect terms:
```csharp
string term = context.Lexicon.GetTerm("Host.Status.Online", defaultValue: "Online");
```

### Desktop Notifications (`IPluginNotificationService`)

Plugins can emit operator notifications with severity levels (Info, Warning, Error):
```csharp
context.Notifications.ShowNotification(
    title: "High Latency Warning",
    message: "Node 'Edge-1' RTT exceeded 150ms.",
    severity: NotificationSeverity.Warning
);
```

---

## Plugin Lifecycle & Execution Flow

```mermaid
sequenceDiagram
    participant Host as MADTOM.Studio
    participant Ctx as StudioHostContext
    participant Plugin as IPluginModule

    Host->>Ctx: Create host context with Tray, Theme, Lexicon
    Host->>Plugin: InitializeAsync(context)
    Plugin->>Ctx: Register tray sections, subscribe to ThemeChanged
    Host->>Plugin: StartAsync()
    Plugin->>Plugin: Begin background collectors, streams, timers
    Host->>Plugin: CreateMainView()
    Host->>Host: Mount view inside main window navigation
    Note over Host,Plugin: Operating Phase
    Host->>Plugin: StopAsync()
    Plugin->>Plugin: Cancel background tasks, flush buffers
```

1. **`InitializeAsync`**: Construct services, subscribe to host events, register tray menu sections, and load local configurations.
2. **`StartAsync`**: Launch background workers, network listeners, or gRPC telemetry subscriptions.
3. **`CreateMainView`**: Instantiate the primary Avalonia `UserControl` or view model to be hosted in the studio's navigation rail.
4. **`StopAsync`**: Gracefully terminate background threads and flush pending writes before application exit.

---

## Step-by-Step: Creating a New Plugin

To add a new plugin named `MADTOM.Plugins.NetworkTools`:

### 1. Project Setup

Create a new project directory under `MADTOM_DOTNET/src/Plugins/NetworkTools/`:

```bash
mkdir -p MADTOM_DOTNET/src/Plugins/NetworkTools/MADTOM.Plugins.NetworkTools
```

In `MADTOM.Plugins.NetworkTools.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\Core\MADTOM.PluginContracts\MADTOM.PluginContracts.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="12.1.1" />
  </ItemGroup>
</Project>
```

### 2. Implement `IPluginModule`

```csharp
using MADTOM.PluginContracts;

namespace MADTOM.Plugins.NetworkTools;

public class NetworkToolsPluginModule : IPluginModule
{
    public string Id => "network-tools";
    public string DisplayName => "Network Tools";
    public string Version => "1.0.0";
    public string Description => "Packet analysis and connectivity diagnostics.";
    public object? Icon => null;

    private IPluginHostContext? _context;

    public Task InitializeAsync(IPluginHostContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public object CreateMainView() => new NetworkToolsMainView();
}
```

### 3. Register Custom System Tray Section

If your plugin provides status indicators, implement `ITrayMenuSection`:

```csharp
public class NetworkToolsTraySection : ITrayMenuSection
{
    public string Header => "Network Status";
    public IReadOnlyList<ITrayMenuItem> Items => new[]
    {
        new TrayMenuItem("Interfaces: 4 Active", isEnabled: false),
        new TrayMenuItem("Ping Gateway: 1.2ms", isEnabled: false)
    };
}
```

And register it during `InitializeAsync`:
```csharp
if (context.Tray != null)
{
    context.Tray.AddSection(new NetworkToolsTraySection());
}
```

### 4. Add to Solution & Host

1. Add the project to `MADTOM_DOTNET/MADTOM.sln` and `MADTOM.slnx` under `/src/Plugins/NetworkTools/`.
2. Add corresponding tests under `MADTOM_DOTNET/MadTOM.Tests/Plugins/NetworkTools/`.
3. Add a project reference from `MADTOM.Studio.csproj` (or configure dynamic assembly loading).
4. Add documentation under `Documentation/Plugins/NetworkTools/`.

This ensures that MADTOM remains completely modular and organized as the ecosystem of plugins grows.

