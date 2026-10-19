# Video Feed Center

Video Feed Center receives IPCam streams and displays a live preview. Phase 1 runs the server inside the plugin or standalone UI, using local TCP for viewer IPC and TLS TCP for camera control.

## Guides

- [Architecture and ownership](engine-and-daemon.md)
- [UI and preview](ui-and-viewer.md)
- [Ingest and recording scope](ingest-and-recording.md)
- [Linux and Android validation](phase1-testing.md)

## Run

From the repository root:

```bash
dotnet run --project MADTOM_DOTNET/src/Plugins/VideoFeedCenter/MADTOM.Plugins.VideoFeedCenter.App
```

Or open Video Feed Center in Studio. Open pairing, copy the `madtom://pair?...` URI, and paste it into IPCam. The paired source connects and starts streaming. Run one hub per machine on the default port 7443.
