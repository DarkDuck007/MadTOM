# MADTOM Studio: desktop and direct-display implementation plan

Status: Proposed; implementation has not started.

Application: `MADTOM_DOTNET/src/Host/MADTOM.Studio`

## Objective

Run MADTOM Studio with the same shared UI in both a desktop environment and on a Linux device without a display server. The latter mode will use Avalonia's built-in DRM/KMS output with GPU acceleration and a connected display.

This is direct-display operation, not an offscreen or displayless execution mode. It requires compatible Linux graphics drivers and hardware. Existing desktop operation must remain supported.

## Current state

- Studio targets .NET 10 and references Avalonia 12.1.1.
- `Program.cs` always calls `StartWithClassicDesktopLifetime(args)` and uses `UsePlatformDetect()`.
- `App.axaml.cs` creates the host context, registers plugins, creates the view model, and installs shutdown cleanup only inside the desktop-lifetime branch.
- `Views/MainWindow.axaml` contains the Studio shell. The window has a minimum size of 1100 × 700, and its content uses the application's `UiScale` transform.
- `Views/MainWindow.axaml.cs` implements close/minimize-to-tray behavior and shared UI handling such as sidebar resizing.
- The plugin contract exposes `Control CreateView()`, allowing plugin views to be reused in a single-view host.
- Telemetry compression diagnostics create a separate window and require a `Window` owner.
- Squeeze uses native storage pickers and accesses the clipboard.

Adding a DRM startup call alone is insufficient: the current initialization would skip the Studio UI and plugins under a single-view lifetime.

## Intended architecture

| Concern | Desktop mode | Direct-display mode |
| --- | --- | --- |
| Startup | `StartWithClassicDesktopLifetime(args)` | `StartLinuxDrm(...)` on Linux |
| Application lifetime | `IClassicDesktopStyleApplicationLifetime` | `ISingleViewApplicationLifetime` |
| Root UI | `MainWindow` hosting `StudioView` | `MainView` assigned a `StudioView` |
| Host and plugins | Shared initialization and view model | Shared initialization and view model |
| Window management | Desktop window behavior | One view filling the display |
| Desktop integrations | Available according to platform capabilities | In-app alternatives or unavailable controls hidden/disabled |
| Shutdown | Shared cleanup initiated by desktop exit | Shared cleanup initiated by explicit exit or service stop |

Separate executables or duplicated plugin UIs are not required. Architecture-specific publish outputs will still be needed.

## Phase 1: output selection and dependencies

1. Add `Avalonia.LinuxFramebuffer` to Studio, matching the Avalonia version used throughout the application. The package includes DRM output despite its name.
2. Retain desktop mode as the default. Introduce an explicit application-owned output option, for example `--output desktop|drm`; finalize the spelling during implementation.
3. On Linux, route DRM mode to `StartLinuxDrm(...)`. Reject DRM selection on other operating systems with an actionable error.
4. Keep fonts, themes, logging, and other common builder configuration shared. Apply backend-specific options only where relevant.
5. Support configuration for the DRM card path and display scaling. Consider connector selection and orientation where required by the target hardware and supported by the pinned Avalonia API.
6. Log the selected backend and effective display configuration. Report device-access and graphics-initialization failures clearly.

Do not rely on `UsePlatformDetect()` to select DRM. Avoid silently falling back to DRM after a desktop startup failure. Optional automatic selection can be considered later, but missing `DISPLAY` or `WAYLAND_DISPLAY` variables alone do not establish that a display is available for takeover.

Acceptance: the output option selects the intended startup path, desktop remains the default, and invalid selections fail clearly before partial application initialization.

## Phase 2: shared Studio view

1. Extract the shell content from `Views/MainWindow.axaml` into a `StudioView` UserControl.
2. Move shell styles and view-specific event handlers, including sidebar resizing, into that view.
3. Reduce `MainWindow` to a desktop wrapper hosting `StudioView`.
4. Keep window state, activation, close interception, and tray restoration in the desktop wrapper.
5. Assign the shared view directly to `ISingleViewApplicationLifetime.MainView` in DRM mode. Add an extra embedded wrapper only if it has a concrete purpose.
6. Preserve existing bindings, resources, navigation, plugin content mounting, and settings behavior.

Acceptance: both lifetimes display the same Studio shell and can navigate all registered plugin views without duplicating their UI definitions.

## Phase 3: shared host initialization and shutdown

1. Move host-context construction, plugin registration, and `ConsoleMainViewModel` creation outside the desktop-only branch in `App.axaml.cs`.
2. Initialize the host once, then connect the shared view model to the selected presentation.
3. Keep tray setup conditional on desktop capabilities.
4. Introduce a common shutdown coordinator that disposes plugins exactly once and awaits asynchronous cleanup before process exit where the runtime permits.
5. Connect desktop exit, direct-display exit, and service termination to that cleanup path using APIs supported by the pinned Avalonia version.
6. Handle partial startup failure so already-initialized plugins are cleaned up.

Do not depend solely on the existing asynchronous `desktop.Exit` event handler for cleanup in both modes. Verify cancellation and termination behavior against Avalonia's actual DRM lifetime implementation.

Acceptance: plugins initialize once in either mode, and controlled exit/service stop completes cleanup without abandoned background work.

## Phase 4: desktop-dependent functionality

| Existing functionality | Planned adaptation |
| --- | --- |
| System tray icon and native tray menu | Create only when supported. Keep important plugin actions accessible through Studio's in-app UI. |
| Close/minimize-to-tray settings | Make unavailable in direct-display mode. Do not hide the only graphical surface. Avoid overwriting the user's desktop preferences merely because DRM mode is active. |
| Application exit | Provide a visible or deliberately configured in-app exit action suitable for the device, backed by shared shutdown. |
| Telemetry compression diagnostics | Extract reusable diagnostics content and display it in an in-app panel/overlay under DRM. Retain the desktop window where appropriate. |
| Squeeze open/save pickers | Check storage capabilities and provide an in-app file browser or explicit path-entry workflow when native dialogs are unavailable. |
| Clipboard | Check capabilities and provide useful unavailable-state behavior or an alternative workflow. |
| Other native windows and OS integrations | Audit for window-owner assumptions, native dialogs, browser/process launching, drag-and-drop, and other desktop services; adapt only the features actually present. |

Known inspection targets:

- `MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry/Views/SidebarView.axaml.cs`: `ShowCompression` currently returns unless its top-level is a `Window`.
- `MADTOM_DOTNET/src/Plugins/Squeeze/MADTOM.Plugins.Squeeze/Views/MainView.axaml.cs`: native open/save picker calls.
- `MADTOM_DOTNET/src/Plugins/Squeeze/MADTOM.Plugins.Squeeze/Views/MainView.Layout.cs`: clipboard access.
- Studio's `App.axaml.cs`, `Views/MainWindow.axaml.cs`, settings UI, and tray service integration.

Prefer capability checks over spreading output-mode checks through plugin business logic. Existing plugin views already return Avalonia controls and should remain shared wherever possible.

Acceptance: diagnostics and essential file workflows remain usable under DRM; unsupported desktop services cause neither exceptions nor silently inaccessible actions.

## Phase 5: layout, scaling, and input

1. Validate the shared shell and plugins at the target device's physical resolution and effective logical size.
2. Review the current 1100 × 700 desktop minimum rather than transferring it blindly to the embedded view.
3. Ensure sidebar collapse, scrolling, dialogs, and navigation remain accessible on smaller displays.
4. Coordinate `UiScale` with DRM display scaling to avoid unintended double scaling.
5. Validate keyboard, mouse, and touch input through the direct-display backend.
6. Ensure important controls do not require hover. Provide suitable touch targets and an on-screen keyboard if the deployment has no physical keyboard.
7. If rotation is required, configure orientation and verify that touch coordinates match it.

Acceptance: all essential controls are reachable at the supported resolutions and input configurations, including text entry on touch-only devices.

## Phase 6: Linux device preparation and deployment

1. Confirm the target has a KMS-capable graphics driver and an available display connector.
2. Install the required native graphics and input libraries: DRM support, GBM, EGL/OpenGL ES drivers, and libinput, plus the application's other native dependencies. Exact package names depend on the distribution.
3. Verify the graphics stack with a tool such as `kmscube` before diagnosing Studio-specific rendering problems.
4. Configure the application's service account to access the required GPU and input devices. Use appropriate device permissions, groups, or session configuration rather than assuming root execution is necessary.
5. Ensure a desktop compositor is not retaining ownership of the intended DRM display. Test console/TTY handling, cursor suppression, and display restoration on exit as required by the target setup.
6. Publish for the device's runtime identifier, such as `linux-arm64` or `linux-x64`. Prefer an initial self-contained deployment to avoid requiring a separately installed .NET runtime.
7. Begin with a straightforward publish configuration. Treat trimming, single-file packaging, and AOT as separate optimizations requiring validation of plugin loading and native dependencies.
8. Configure appliance startup using a service with explicit DRM mode, a known working directory, writable settings paths, device access, logging, and appropriate restart behavior.
9. Verify graceful service stop, restart, and cold boot. Match any console-input handling to service execution; do not assume an interactive terminal is available.

Self-contained publishing does not bundle the complete native Linux graphics stack. Direct scanout also does not by itself prove rendering is hardware accelerated: verify the selected renderer/driver on the device and check for software rendering.

Acceptance: Studio starts reliably after boot without a display server, renders with the intended GPU driver, accepts input, and stops/restarts cleanly under the service account.

## Desktop Linux and Wayland scope

Studio's existing `UsePlatformDetect()` path uses X11 on Linux and can operate on Wayland desktops through XWayland. A Wayland-only desktop without XWayland is an additional compatibility case.

If that case is required, add the matching `Avalonia.Wayland` package and select `UseWayland()` conditionally. Avalonia's documentation currently describes native Wayland support as experimental and states that `UsePlatformDetect()` does not select it automatically. Recheck this status against the version selected for implementation.

Native Wayland support is separate from the DRM implementation and should not block the initial desktop-plus-DRM milestone unless it is a deployment requirement.

## Validation matrix

| Environment or scenario | Required checks |
| --- | --- |
| Existing supported desktop platforms | Startup, plugin navigation, shell layout, tray actions, close/minimize behavior, settings, and shutdown |
| Linux X11 / Wayland with XWayland | Existing desktop path, dialogs, diagnostics windows, input, and clipboard |
| Linux DRM on target hardware | Startup without a display server, correct card/output, rendering, verified acceleration, input, and plugin navigation |
| Direct-display feature workflows | Telemetry diagnostics, Squeeze open/save operations, in-app actions replacing tray access |
| Small display and touch-only setup | Scaling, scrolling, dialogs, touch targets, text entry, and rotation if supported |
| Failure cases | Inaccessible GPU/input devices, invalid card selection, unavailable output, and graphics startup failure produce useful diagnostics |
| Lifecycle | Controlled exit, partial startup failure, service stop/restart, and cold boot |
| Native Wayland, if included | Explicit backend selection and regression checks for supported desktop features |

Use focused automated checks for backend selection, capability-based behavior, and shared lifecycle handling where practical. Real-device testing is required for DRM ownership, graphics drivers, scanout, and input; desktop or headless UI tests cannot establish those properties.

## Expected implementation areas

- Studio project file: backend package reference(s).
- `Program.cs`: output selection and backend configuration.
- `App.axaml.cs`: common initialization, lifetime routing, and cleanup.
- `Views/MainWindow.axaml` and code-behind: desktop wrapper.
- New shared `StudioView`: shell content and view behavior.
- Host/settings integration: desktop capabilities, tray alternatives, and exit handling.
- Telemetry diagnostics presentation: embedded alternative.
- Squeeze storage and clipboard workflows: capability-aware handling.
- Deployment assets and documentation: publish instructions, device requirements, output options, and service setup.

During implementation, synchronize affected project documentation using the repository's documentation workflow. This plan does not change application behavior or establish final CLI/configuration names.

## Completion criteria

- One shared Studio UI runs through both desktop and DRM startup paths.
- Existing desktop behavior remains functional.
- Plugins initialize and shut down correctly in both modes.
- Essential plugin workflows do not depend on unavailable desktop services.
- The intended Linux device runs Studio without a display server using verified GPU acceleration.
- Supported resolution/input combinations and boot/service lifecycle pass validation.
- User-facing configuration and deployment requirements are documented.

## References

- [Avalonia embedded Linux: DRM output, single-view lifetime, scaling, and input](https://docs.avaloniaui.net/docs/platform-specific-guides/embedded-linux)
- [Avalonia LinuxFramebuffer startup API](https://docs.avaloniaui.net/api/global/linuxframebufferplatformextensions)
- [Avalonia Raspberry Pi guide: shared desktop and embedded views](https://docs.avaloniaui.net/docs/platform-specific-guides/embedded-linux/raspberry-pi)
- [Avalonia embedded Linux deployment](https://docs.avaloniaui.net/docs/deployment/embedded-linux)
- [Avalonia desktop Linux and native Wayland support](https://docs.avaloniaui.net/docs/platform-specific-guides/linux)

Review version-specific APIs and distribution-specific deployment details when implementation begins.
