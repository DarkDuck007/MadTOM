# Video Feed Center: embedded engine

## Contents

- [Ownership and transport](#ownership-and-transport)
- [Daemon versus embedded engine](#4-daemon-versus-embedded-engine)
- [Failure reporting](#failure-reporting)

## Ownership and transport

Video Feed Center owns one `FeedEngineHost` inside the plugin or standalone UI process. Opening the view again reuses the view and server. Shutdown stops the host and its FFmpeg children.

The viewer uses HTTP/2 gRPC over an ephemeral **loopback TCP** port. Pairing and camera control use TLS on TCP port **7443**. Linux sends MPEG-TS over SRT; Android sends MPEG-TS over UDP. Ingest ports start at 8000. No Unix socket, named pipe, process manifest, or daemon discovery is used by this plugin.

FFmpeg remains an external multimedia worker, not a MADTOM daemon. Install an FFmpeg build with V4L2, libx264, and SRT support on Linux.

## 4. Daemon versus embedded engine

Phase 1 uses the embedded engine. The legacy `MADTOM.Feed.Daemon` project remains in the source tree for compatibility, but is not started or discovered by the plugin. Do not run it alongside the feed center: both would attempt to bind TCP 7443. No system service is required for phase 1.

## Failure reporting

Server startup failures appear in the view. Pairing QR codes are generated only by the running server. FFmpeg failures are retained in the feed error; ended inputs restart the listener. Feed state and profile updates are broadcast to the UI.

See [phase 1 validation](phase1-testing.md) for Linux and Android steps.
