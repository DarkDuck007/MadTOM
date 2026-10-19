# Video ingest and phase 1 scope

## Contents

- [Ingest](#ingest)
- [Preview path and bandwidth](#6-preview-path-and-bandwidth)
- [Latency measurement](#7-latency-measurement)
- [Recording](#recording)

## Ingest

Linux IPCam encodes with FFmpeg and sends MPEG-TS to an SRT listener. Android uses Camera2 and MediaCodec, packetizing MPEG-TS into UDP datagrams of at most seven TS packets (1316 bytes). The hub chooses its receiving interface address for the source target.

The default hub request is H.264, 1280×720 at 30 fps. Desktop source preferences are applied when connecting and reported back as the granted profile. Linux capture uses the camera's default native input mode and scales the output; the desktop resolution list describes encoded output choices rather than an enumeration of sensor modes.

Ingest decodes only the selected video stream to a 640×360 BGRA preview. No audio is captured in phase 1. A live state requires an actual decoded frame. FFmpeg stderr is retained on failure. Ended listeners are restarted for reconnection.

## 6. Preview path and bandwidth

Preview frames are uncompressed and stay on loopback TCP. Each viewer has an independent queue holding one pending frame. The Avalonia renderer also retains only the latest pending frame to avoid accumulating UI work.

## 7. Latency measurement

The former typed millisecond measurement UI is removed. Clock synchronization RTT is control-channel timing, not glass-to-glass latency. Automated video latency measurement remains future work.

## Recording

Automatic original/proxy recording is disabled for phase 1. Preview must not depend on recording directories, segment encoders, or sidecar generation. Legacy asset types remain available to other code; no recording files or fabricated asset manifests are produced by phase 1 ingest.
