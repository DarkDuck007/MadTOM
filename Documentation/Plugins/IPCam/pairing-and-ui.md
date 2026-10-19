# IPCam pairing, capture, and UI

## Contents

- [Linux camera discovery](#linux-camera-discovery)
- [Preview and streaming](#preview-and-streaming)
- [Pairing](#pairing)
- [Android](#android)
- [Validation](#validation)

## Linux camera discovery

IPCam enumerates `/sys/class/video4linux` and queries V4L2 capture and streaming capabilities with `VIDIOC_QUERYCAP`. It excludes metadata nodes without capturing probe frames. The synthetic test pattern is always available. Camera permissions and availability are reported when capture starts.

The view is initialized once. Switching tabs neither rediscovers nor reopens cameras. Choose a camera and explicitly start preview. Stop preview releases preview-only capture; an active hub stream continues until disconnected.

## Preview and streaming

FFmpeg produces a 640×360 BGRA local preview at 15 fps. When connected, one capture process also encodes video and sends MPEG-TS over SRT. Width, height, FPS, codec and bitrate preferences are applied on connection; reconnect after changing them. The granted profile describes encoded output. Linux camera input uses the device default mode; the UI resolution list contains output scaling choices, not measured sensor modes.

First-run encoding defaults are AVC/H.264, 1280×720, 30 fps. Existing saved settings are retained. FFmpeg startup and device errors appear in IPCam status. A stream start waits for a preview frame before acknowledging success. Disposing a connection does not dispose the shared camera engine.

Frames enter the shared viewer through a bounded mailbox. Bitmap creation and updates happen on the Avalonia dispatcher. No camera worker accesses the visual tree.

## Pairing

Open Video Feed Center first. Copy its `madtom://pair?...` URI into IPCam, or scan its QR using the active preview. The URI includes the hub address, certificate fingerprint, session ID, and one-use token. Successful pairing persists trust and connects the source. Use QR/URI pairing for phase 1; the unfinished manual PIN flow is hidden.

The hub runs within Video Feed Center. No separate MADTOM daemon or Unix socket is required. Local viewer IPC uses loopback TCP; camera control uses TLS TCP 7443.

## Android

The activity owns one Camera2 engine for the TextureView preview and MediaCodec stream. Camera callbacks use an explicit main-looper handler. The activity passes an existing preview surface after initialization, covering the surface-created-before-permission race.

The encoder sends MPEG-TS over UDP in datagrams of at most 1316 bytes and repeats codec configuration with keyframes. Disconnect before changing cameras. Keep the activity visible for phase 1; foreground-service background streaming is excluded from this build.

## Validation

See [Linux and Android validation](../VideoFeedCenter/phase1-testing.md). The old manually entered millisecond latency UI has been removed. Phase 1 does not claim glass-to-glass latency or record video.
