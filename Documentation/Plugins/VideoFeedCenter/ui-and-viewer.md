# Video Feed Center UI and preview

## Contents

- [Startup](#startup)
- [Pairing](#pairing)
- [Preview](#preview)
- [Diagnostics](#diagnostics)

## Startup

The first view initialization starts the embedded server. Switching tabs reuses the view and does not restart the server. The status text reports startup failures, including TCP port conflicts.

## Pairing

Open pairing and copy its `madtom://pair?...` URI into IPCam. The QR URI pins the server certificate and carries a single-use token valid for five minutes. Pairing requires a running server; no fallback QR is fabricated. Use QR/URI pairing for phase 1; the unfinished manual comparison-code flow is hidden in IPCam.

## Preview

Select a source from the feed list. The selected source streams 640×360 BGRA preview frames through local TCP gRPC. Each subscriber has its own bounded latest-frame queue. The viewer copies incoming frame data and creates, updates, and renders its bitmap on the Avalonia UI thread. Producer threads never look up controls in the visual tree.

Tab switches reuse the same view. Camera capture and stream ownership belong to the source, not view visibility.

## Diagnostics

The feed shows state, requested and granted stream formats, source statistics, and ingest errors. The manual millisecond clock, typed counter, and delay calculation button have been removed. No glass-to-glass latency claim is made. Phase 1 does not record video.

See [validation](phase1-testing.md).
