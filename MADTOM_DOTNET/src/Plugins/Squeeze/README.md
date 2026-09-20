# SQUEEZE UI

SQUEEZE is MADTOM's remote media transcoder plugin. The client connects to a SQUEEZE server; encoding runs on the server.

## Workflow

1. Open **Settings**, connect to a server, or discover a LAN node.
2. Click or tap the **source media bar** to choose a video or drop a file onto the workspace.
3. Open **Presets** and select a profile, then adjust Video, Dimensions, Filters, Output, or Audio settings as needed.
4. Choose **Start encode** to upload and enqueue the current settings.
5. Select a job name to inspect its settings. Use the job queue to pause, resume, cancel, or download completed output. **Clear done** removes completed jobs through the existing server action.

## Responsive workspace

At widths below 760 logical pixels, **Encode settings** and the job-count button switch between full-width editing and the queue. Parameter labels stack above their inputs, tabs and toolbar actions wrap, and touch controls have at least 40-pixel height; desktop buttons use compact 30-pixel minimum heights with centered icons and labels. Wider layouts retain the resizable, collapsible queue sidebar; its width and collapse state survive switching to a narrow layout and back.

## Preset catalog

- Search across names, descriptions, codecs, containers, categories, tags, and resolution labels. Searches ignore case and whitespace; every word must match somewhere in the profile.
- Presets are separated into expandable groups, collapsed initially. Filtering expands matching groups; clearing filters restores the previous expansion state. Filter by category, including **Custom** and categories received from the server. **Reset** clears both filters. An empty result explains how to recover.
- All overlays (presets, hardware, server connection, and save preset) support desktop edge/corner resizing, visible corner grips, and Escape to close. On desktop, drag any edge or corner to resize the centered catalog. Its size stays within the plugin viewport and is remembered while the view remains alive. **Reset size** restores the initial size.
- On narrow screens the catalog fills the available viewport with an internally scrolling, grouped list.
- Opening the catalog focuses search. **Escape** closes an open dialog; keyboard focus returns to the previous control.
- **Save preset** captures the current parameters as a named custom profile, with a group selector, immediately available in the chosen group. User profiles in any group survive server catalog refreshes. **Set default** designates a saved profile for startup. Modified settings must first be saved; existing preset persistence semantics are unchanged.

The FFmpeg preview has a **Copy** action. Job errors and download progress/results appear as readable messages rather than requiring hover.

## Theme and integration

The UI uses the existing dynamic MADTOM brush resources and `SqueezeThemeService` host synchronization. Layout behavior uses the plugin view's available size, so it works in both the MADTOM host and the standalone app. File staging, HTTP backend, job commands, plugin lifecycle, and transcoding parameter serialization retain their existing implementations.

## Build and verify

From this directory:

```sh
dotnet build MADTOM.Plugins.Squeeze.App/MADTOM.Plugins.Squeeze.App.csproj
dotnet run --project MADTOM.Plugins.Squeeze.UiChecks
```

The UI checks use Avalonia's headless backend to exercise viewport sizes, navigation, dialog resizing, preset filtering, and dynamic theme changes. Pass an output directory after `--` to save screenshots:

```sh
dotnet run --project MADTOM.Plugins.Squeeze.UiChecks -- /tmp/squeeze-ui-shots
```

The project's existing client guide is at [SQUEEZE Client Guide](../../../../Documentation/Plugins/Squeeze/CLIENT_GUIDE.md). Physical-device touch, software-keyboard behavior, and a live server encode still require integration testing.
