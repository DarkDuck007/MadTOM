# SQUEEZE HTTP & Discovery Protocol Specification

This document defines the REST API, Server-Sent Events (SSE) streaming protocols, and mDNS network discovery contracts for SQUEEZE.

---

## 1. Authentication

When the server is configured with a token (`--token <secret>` or `server.token` in YAML):
- All requests (except `GET /api/v1/health` and mDNS queries) require the `Authorization` header:
  ```http
  Authorization: Bearer <token>
  ```
- Unauthenticated or invalid token requests receive HTTP `401 Unauthorized`.

---

## 2. API Endpoints

### 2.1 System & Hardware

#### `GET /api/v1/health`
Retrieves server health, CPU/memory telemetry, active job counts, and verified hardware acceleration encoders.

**Response `200 OK`**:
```json
{
  "status": "ok",
  "version": "1.0.0",
  "node_id": "RIG_ALPHA_01",
  "uptime_seconds": 1284.5,
  "cpu_cores": 16,
  "process_memory_bytes": 48234496,
  "max_concurrent": 4,
  "jobs": {
    "queued": 0,
    "processing": 1,
    "completed": 5,
    "error": 0
  },
  "encoders": ["libx264", "libx265", "hevc_vaapi", "h264_vaapi"],
  "hardware_encoders": ["hevc_vaapi", "h264_vaapi"],
  "hardware_status": "active_hardware_verified (2 active: hevc_vaapi, h264_vaapi)",
  "pause_supported": true
}
```

#### `POST /api/v1/hardware/rescan`
Forces cache invalidation and executes a fresh hardware probe across all candidate encoders.

**Response `200 OK`**:
```json
{
  "status": "ok",
  "hardware_status": "active_hardware_verified (2 active: hevc_vaapi, h264_vaapi)",
  "hardware_encoders": ["hevc_vaapi", "h264_vaapi"]
}
```

---

### 2.2 Presets

#### `GET /api/v1/presets`
Lists all available transcoding presets configured on the server.

**Response `200 OK`**:
```json
[
  {
    "key": "fast1080",
    "title": "Fast 1080p30 (Default)",
    "category": "General",
    "description": "Standard H.264 high-speed delivery profile.",
    "tag": "DEFAULT",
    "spec": {
      "video_codec": "libx264",
      "crf": 22,
      "preset": "medium",
      "container": "mp4",
      "audio_codec": "aac",
      "audio_bitrate": 160
    }
  }
]
```

#### `GET /api/v1/presets/{key}`
Retrieves details for a specific preset identified by `key`.

---

### 2.3 Job Management & Lifecycle

#### `POST /api/v1/jobs`
Registers a new encoding job and fixes the expected file size before upload.

**Request Body**:
```json
{
  "filename": "sample_video.mp4",
  "file_size": 154829104,
  "spec": {
    "video_codec": "libx264",
    "crf": 22,
    "container": "mp4",
    "audio_codec": "aac",
    "audio_bitrate": 160,
    "width": 1920,
    "height": 1080,
    "fps": 30.0
  }
}
```

**Response `201 Created`**:
```json
{
  "job_id": "job_01hxyz...",
  "status": "created",
  "filename": "sample_video.mp4",
  "file_size": 154829104,
  "upload_offset": 0,
  "upload_url": "/api/v1/jobs/job_01hxyz.../upload",
  "events_url": "/api/v1/jobs/job_01hxyz.../events"
}
```

#### `GET /api/v1/jobs`
Lists all active and recently completed jobs.

#### `GET /api/v1/jobs/{id}`
Returns status and metadata for job `{id}`.

#### `DELETE /api/v1/jobs/{id}`
Cancels an active or waiting job and deletes temporary files.

#### `POST /api/v1/jobs/{id}/start`
Manually starts/retries an uploaded job.

#### `POST /api/v1/jobs/{id}/pause`
Pauses an active encoding process (via `SIGSTOP` on Unix systems).

#### `POST /api/v1/jobs/{id}/resume`
Resumes a paused encoding process (via `SIGCONT` on Unix systems).

---

### 2.4 Media Transfer & Streaming

#### `POST /api/v1/jobs/{id}/upload`
Streams media bytes into scratch storage. Supports standard and chunked resumable transfers.

**Headers**:
- `Content-Type: application/octet-stream`
- `Content-Range: bytes <start>-<end>/<total>` (optional for chunked resume)
- `Content-Length: <bytes>`

Once all bytes matching `file_size` are received, the server automatically transitions the job from `uploading` to `queued`.

#### `GET /api/v1/jobs/{id}/download`
Streams the finished transcode output file (`Content-Disposition: attachment`).

#### `GET /api/v1/jobs/{id}/events`
Server-Sent Events (SSE) stream delivering real-time transcode progress.

**Event Stream Format**:
```http
event: progress
data: {"job_id":"job_01...","percent":42.5,"fps":58.2,"eta_seconds":35,"frame":1240,"bitrate_kbps":4500}

event: completed
data: {"job_id":"job_01...","output_size":74218042,"download_url":"/api/v1/jobs/job_01.../download"}
```

---

## 3. Network Discovery (mDNS & Subnet Broadcast)

### Service Registration
- **Service Name**: `SQUEEZE <node_id>`
- **Service Type**: `_squeeze._tcp`
- **Domain**: `local.`

### TXT Records

| Key | Example Value | Description |
| :--- | :--- | :--- |
| `version` | `1.0.0` | SQUEEZE server semantic version. |
| `node_id` | `RIG_ALPHA_01` | Unique node identifier. |
| `gpu` | `hevc_vaapi,h264_vaapi` | Comma-separated list of active hardware acceleration encoders. |
| `path` | `/api/v1` | Root API base path. |
| `auth` | `false` | Indicates whether bearer token authentication is enabled. |
| `port` | `8080` | TCP listening port. |
| `ip` | `10.217.170.39` | Primary RFC 1918 private IPv4 address. |
| `ips` | `10.217.170.39,192.168.1.5` | Comma-separated list of all valid private LAN interfaces. |

