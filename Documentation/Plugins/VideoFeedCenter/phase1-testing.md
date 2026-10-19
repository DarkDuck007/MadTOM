# Phase 1 video validation

## Contents

- [Automated Linux checks](#automated-linux-checks)
- [Linux UI and hardware](#linux-ui-and-hardware)
- [Android](#android)
- [Limits](#limits)

## Automated Linux checks

Run from the repository root with .NET 10 and FFmpeg installed. Tests need permission to open loopback TCP and UDP sockets.

```bash
dotnet test MADTOM_DOTNET/MadTOM.Tests/Plugins/IPCam/MADTOM.Plugins.IPCam.Tests.csproj -m:1
dotnet test MADTOM_DOTNET/MadTOM.Tests/Plugins/VideoFeedCenter/MADTOM.Feed.Engine.Tests.csproj -m:1
```

`DesktopStreamingTests` uses real FFmpeg processes: synthetic camera → H.264 MPEG-TS → SRT → hub decode. It requires both local preview and two independent hub subscribers to receive frames, and verifies that preview does not claim to be recording.

## Linux UI and hardware

1. Start Video Feed Center in Studio or its standalone app. Confirm the server-ready status.
2. Start IPCam in Studio or `dotnet run --project MADTOM_DOTNET/src/Plugins/IPCam/MADTOM.Plugins.IPCam.App`.
3. Select **Synthetic Color Bar Test Pattern**, then **Start preview**. Confirm moving video. This separates pipeline problems from hardware problems.
4. Copy the feed center pairing URI into IPCam's pairing dialog. Confirm both local and hub preview, and a Live feed state.
5. Disconnect, select the normal camera, and start preview. Repeat with the IR device if present. Metadata-only V4L2 nodes should not appear. Discovery uses `VIDIOC_QUERYCAP`, without streaming test frames from each device.
6. Switch tabs repeatedly. Camera selection and server ownership should remain stable. Only explicit preview or stream actions activate capture.
7. Run for at least ten minutes, then disconnect and reconnect. Confirm no UI thread exception, duplicated streams, or orphan FFmpeg processes after closing the app.
8. Test with a busy camera or missing FFmpeg. Confirm an actionable error rather than a silent empty preview.

## Android

Build with the .NET Android SDK and Android SDK installed:

```bash
dotnet build MADTOM_DOTNET/src/Plugins/IPCam/MADTOM.Plugins.IPCam.Android -m:1
```

Install the signed debug APK from `bin/Debug/net10.0-android/`. Keep the app visible for phase 1. Grant camera permission, choose a camera, and confirm the TextureView preview. Paste the Linux hub's pairing URI, then tap **Connect and stream**. Confirm the same camera appears locally and in the hub. Disconnect before switching cameras. Test permission denial, reconnect, and activity closure on a physical device.

The activity owns a single camera engine shared by preview and streaming. It no longer starts a second camera engine in a foreground service. Background streaming is outside this phase's validation scope.

## Limits

Synthetic tests establish desktop capture/transport/decode behavior. Physical RGB/IR camera compatibility, a sustained GUI session, and Android Camera2/MediaCodec behavior require device validation. Android uses UDP for media; allow the allocated hub UDP ingest port through the firewall. TLS control uses TCP 7443.
