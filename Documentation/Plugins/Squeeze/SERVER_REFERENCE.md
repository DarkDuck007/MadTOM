# SQUEEZE Server Reference Manual

This manual provides reference details for configuring, running, and managing the `SQUEEZE_SERVER` daemon.

---

## 1. Quick Start

### Build

From repository root:
```sh
# Build using MADTOM multi-architecture Go build script
./MADTOM_GOLANG/build.sh -p squeeze

# Or using the unified root build script
./build.sh
```

### Run

```sh
# Run with sample configuration
./MADTOM_GOLANG/bin/squeeze-server -c MADTOM_GOLANG/configs/squeeze/squeeze.yaml

# Run directly with command-line flags
./MADTOM_GOLANG/bin/squeeze-server -port 8080 -workers 4 -node-id RIG_ALPHA_01

# Run as a systemd service
sudo cp MADTOM_GOLANG/systemd/squeeze-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now squeeze-server
```

---

## 2. Command-Line Arguments & Environment Variables

All command-line flags take precedence over YAML configuration values, which in turn take precedence over environment variables and default values.

| Flag | Shorthand | Environment Variable | Default Value | Description |
| :--- | :--- | :--- | :--- | :--- |
| `--config` | `-c` | `SQUEEZE_CONFIG` | `""` | Path to YAML configuration file. |
| `--host` | | `SQUEEZE_HOST` | `0.0.0.0` | HTTP bind network address. |
| `--port` | | `SQUEEZE_PORT` | `8080` | HTTP listening port (1-65535). |
| `--workers` | | `SQUEEZE_WORKERS` | `1` | Maximum concurrent FFmpeg encoding jobs. |
| `--queue-size` | | `SQUEEZE_QUEUE_SIZE` | `128` | Maximum number of waiting jobs in the queue. |
| `--max-upload` | | `SQUEEZE_MAX_UPLOAD` | `10737418240` (10 GiB) | Maximum allowed upload size in bytes. |
| `--scratch` | | `SQUEEZE_SCRATCH` | `/tmp/squeeze/scratch` | Dedicated temporary directory for active uploads and working files. |
| `--output` | | `SQUEEZE_OUTPUT` | `/tmp/squeeze/output` | Dedicated storage directory for finished transcode outputs. |
| `--retention` | | `SQUEEZE_RETENTION` | `24h` | Retention duration for finished outputs and incomplete uploads before pruning. |
| `--upload-timeout` | | `SQUEEZE_UPLOAD_TIMEOUT` | `30m` | Maximum allowed duration for a single continuous upload transfer. |
| `--node-id` | | `SQUEEZE_NODE_ID` | Hostname | Human-readable node identifier advertised across LAN and mDNS. |
| `--token` | | `SQUEEZE_TOKEN` | `""` | Optional bearer token for API authentication (`Authorization: Bearer <token>`). |
| `--ffmpeg` | | `SQUEEZE_FFMPEG` | `ffmpeg` | Path or binary name for the FFmpeg executable. |
| `--ffprobe` | | `SQUEEZE_FFPROBE` | `ffprobe` | Path or binary name for the FFprobe executable. |
| `--mdns` | | `SQUEEZE_MDNS` | `true` | Enable zero-configuration LAN discovery advertisement via mDNS. |

---

## 3. Configuration File (`config.sample.yaml`)

Configuration can be specified in YAML format:

```yaml
server:
  host: "0.0.0.0"
  port: 8080
  workers: 2
  queue_size: 128
  max_upload: 10737418240 # 10 GiB in bytes
  scratch: "/tmp/squeeze/scratch"
  output: "/tmp/squeeze/output"
  retention: "24h"
  upload_timeout: "30m"
  node_id: "RIG_ALPHA_01"
  mdns: true
  token: "" # Leave empty for unauthenticated LAN mode
  ffmpeg: "ffmpeg"
  ffprobe: "ffprobe"

presets:
  - key: "fast1080"
    title: "Fast 1080p30 (Default)"
    category: "General"
    description: "Standard H.264 delivery profile."
    tag: "DEFAULT"
    spec:
      video_codec: "libx264"
      crf: 22
      preset: "medium"
      container: "mp4"
      audio_codec: "aac"
      audio_bitrate: 160
      width: 1920
      height: 1080
      fps: 30
      deinterlace: true
```

### Configuration Keys Reference

- `host`: The IPv4/IPv6 address to bind to (`0.0.0.0` for all interfaces).
- `port`: TCP port for the HTTP/SSE server.
- `workers`: Number of simultaneous FFmpeg jobs processed concurrently.
- `queue_size`: Buffer capacity for pending jobs.
- `max_upload`: Hard file size limit for uploaded media.
- `scratch`: Working folder where incoming streams and active encode segments live.
- `output`: Folder where finished output media files are stored ready for download.
- `retention`: Time string (`24h`, `30m`) defining when old completed/failed jobs are purged from disk.
- `upload_timeout`: Time string defining max duration per upload connection.
- `node_id`: Name displayed to clients during mDNS scans.
- `mdns`: Boolean enabling or disabling multicast zero-configuration broadcast.
- `token`: Shared secret required in the `Authorization: Bearer <token>` header.
- `ffmpeg`: Path to FFmpeg binary.
- `ffprobe`: Path to FFprobe binary.
- `presets`: Array of server-provided preset templates with encoding parameters.

---

## 4. Hardware Probing & Caching Architecture

### Probing Workflow
When `squeeze-server` starts:
1. **Signature Calculation**: Computes a SHA-256 hash over:
   - Host CPU model and core count (`/proc/cpuinfo` or OS runtime).
   - Host GPU hardware devices (`/sys/class/drm/card*/device/vendor`, `/dev/dri`, `/proc/driver/nvidia/version`).
   - Installed FFmpeg binary version string (`ffmpeg -version`).
2. **Cache Verification**: Checks `hardware_cache.json` in the scratch/cache directory. If the hardware signature matches, the probed capabilities are restored instantly without latency.
3. **Active Encoder Dry-Run**: If the signature changed or cache is absent, the server queries `ffmpeg -encoders` and performs a fast 1-frame dummy dry-run on candidate hardware acceleration encoders:
   - **VA-API**: `hevc_vaapi`, `h264_vaapi`, `av1_vaapi`, `mjpeg_vaapi`
   - **NVENC**: `h264_nvenc`, `hevc_nvenc`, `av1_nvenc`
   - **QuickSync (QSV)**: `h264_qsv`, `hevc_qsv`, `av1_qsv`
   - **AMF**: `h264_amf`, `hevc_amf`, `av1_amf`
4. **Results Cached**: Successfully initialized encoders are cached and reported in `GET /api/v1/health` and mDNS TXT records (`gpu=...`).

### Manual Re-Scan
Clients or administrators can force an immediate hardware re-probe via:

```sh
curl -X POST http://localhost:8080/api/v1/hardware/rescan
```

---

## 5. Storage & Retention Policies

- **Scratch Directory**: Holds partially uploaded chunks (`<job_id>.upload`) and active output segments. Purged immediately on job cancellation or upon error cleanup.
- **Output Directory**: Holds completed target files (`<job_id>.<ext>`).
- **Sweeper Routine**: A periodic background worker runs every minute (or `retention` interval) to delete expired jobs and release disk space.

