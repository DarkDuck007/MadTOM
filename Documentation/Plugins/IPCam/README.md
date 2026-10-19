# MADTOM IPCam

IPCam captures a Linux or Android camera, shows a local preview, and streams to the embedded Video Feed Center server.

- [Pairing, capture, and UI](pairing-and-ui.md)
- [Phase 1 validation](../VideoFeedCenter/phase1-testing.md)
- [Feed center architecture](../VideoFeedCenter/engine-and-daemon.md)

## Run on Linux

```bash
dotnet run --project MADTOM_DOTNET/src/Plugins/IPCam/MADTOM.Plugins.IPCam.App
```

Or open IP Camera in Studio. Choose the synthetic pattern first to validate FFmpeg, then select a physical camera and start preview. Pair using the running feed center's QR URI. Switching tabs reuses the existing capture owner.

## Android

```bash
dotnet build MADTOM_DOTNET/src/Plugins/IPCam/MADTOM.Plugins.IPCam.Android -m:1
```

Install the signed debug APK, grant camera permission, pair using the hub URI, and tap Connect and stream. Keep the activity visible during phase 1 testing.
