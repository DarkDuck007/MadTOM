# MADTOM: NOX AI Control Center Plugin

The **NOX AI Control Center** (`MADTOM.Plugins.NoxAI`) provides a real-time, authenticated, and encrypted control and telemetry workspace dedicated to **NoxChat** (the autonomous multimodal Telegram bot and memory engine in the submodule `NoxChat/`).

---

## Architecture Overview

```
┌────────────────────────────────────────────────────────┐
│               MADTOM Studio (Avalonia C#)              │
│  - 7 Dedicated NOX AI Control Workspaces               │
│  - Dual Zstd Caches (Logs/API vs. Streamed History)   │
│  - SSH-style Auth Generator (`noxchat-ed25519-v1:...`) │
│  - LTTB Downsampled Time-Series Charts                 │
└───────────────────────────┬────────────────────────────┘
                            │ gRPC over TLS (mDNS / IP:port)
                            │ Header: HMAC-SHA256 signature
                            │ Payload: Zstd chunked stream
┌───────────────────────────▼────────────────────────────┐
│          NoxChat_Probe (Go System Daemon)              │
│  - Systemd Service: `noxchat-probe.service`           │
│  - SSH Key Verification & ChaCha20-Poly1305 AEAD       │
│  - 1MB State Ring Buffer + 8MB Log/API Ring Buffer     │
│  - Server-Side Retained Log Literal Search             │
│  - Server-Side WAL Spool (1GB default disk quota)      │
└───────────────────────────┬────────────────────────────┘
                            │ Local Loopback TCP Framing (127.0.0.1:50056)
                            │ Non-127.0.0.1 produces mandatory WARNING
┌───────────────────────────▼────────────────────────────┐
│             NoxChat Python Backend Engine              │
│  - `src/control/server.py`: Async length-prefixed loop │
│  - `src/control/direct_chat.py`: Ingests as "MADTOM"   │
│  - `src/control/telemetry.py`: Python runtime POV (1Hz)│
│  - `src/control/rpc_handlers.py`: RAG/Memories/Media   │
└────────────────────────────────────────────────────────┘
```

---

## The 7 Primary Workspaces

As configured in `MainView.axaml`, the NOX AI Control Center provides 7 dedicated tabbed workspaces in sequential order:

### Workspace 1: 📈 Telemetry & Usage (`NoxTelemetryTabView`)
- **Real-Time 1 Hz Runtime Stream**: Directly measures Python runtime internals:
  - Exact Resident Set Size (RSS) and Virtual Memory Size (VMS) via `/proc/self/statm`.
  - Python CPU utilization percentage across all worker threads.
  - Active `asyncio.all_tasks()` count and event-loop lag monitoring.
  - Python generational GC collection counts (`gc.get_count()`).
  - LLM token throughput: prompt tokens, completion tokens, cached tokens.
- **Dynamic Timeframe Selection**: Switchable time horizons (`5M`, `15M`, `1H`, `6H`, `24H`, `7D`) that automatically query historical telemetry ranges and adjust nanosecond chart boundaries.
- **Multi-Series Visualization**: Powered by `MetricHistoryChartControl` with Largest-Triangle-Three-Buckets (LTTB) downsampling.

### Workspace 2: 💬 Direct Chat (`DirectChatTabView`)
- **Direct Pipeline Ingestion**: Bypasses the Telegram Bot API directly into the NoxChat dialogue engine.
- **Authenticated Operator Persona**: Interacts as authenticated operator `"MADTOM"` (User ID: `"Control Center"`).
- **Live Streamed Reasoning**: Displays streaming thought processes (`<thought>...</thought>`), memory operations, and tag dispatches as the bot reasons in real time.

### Workspace 3: 🧠 RAG & Memories (`MemoriesRagTabView`)
Organized into two dedicated sub-tabs:

#### Sub-Tab 1: 🧠 Memories & Hot Scratchpad
- **Hot Memory Scratchpad**: Real-time view, inspection, and manual clearing of active working memory key-value entries.
- **Atomic Memories Explorer**: Search, filter, and inspect long-term atomic memories stored in SQLite and Embedded Qdrant.
- **Hybrid Dense/Sparse Search**: Search long-term memories using hybrid dense embedding vectors and BM25 sparse rankings with real-time score badges.
- **Vector Point Inspection**: Inspect embedded vectors, cosine distances, payloads, and underlying message context.

#### Sub-Tab 2: 👤 User Profiles & Avatars
- **User Profile Vector Explorer**: Connects to the dedicated Qdrant collection `user_profiles`, tracking Telegram users across chats.
- **Semantic Profile Search**: Vector search over user bios, interests, communication patterns, custom titles, and channel descriptions.
- **Avatar Gallery & Metadata**: Lazily loads profile avatar thumbnails with full user detail cards (user ID, username, title, bio, language, linked channel, birthdate).
- **Fullscreen Avatar Lightbox**: Clicking an avatar opens a high-resolution modal lightbox view for close visual inspection.

### Workspace 4: 🎨 Stickers & GIFs (`MediaLibraryTabView`)
- **Visual Media Grid**: Browses the chat's indexed Telegram stickers, GIFs, and media packs.
- **Dual-Mode Search**: Real-time text-based search (matching sticker set name, `t.me/addstickers/` set URL, or sticker caption/definition/emoji) combined with Enter-triggered semantic embedding search via `MediaVectorStore` / Qdrant.
- **Force Add Sticker Panel**: Collapsible form allowing operators to manually force-index and caption individual stickers by message ID, custom caption, or set name.
- **Sticker Pack Analysis**: Triggers manual background Sticker Analyst map-reduce jobs.
- **Annotation & Reaction Curation**: Inspect, pin/unpin favorite reactions, and edit semantic tag annotations.

### Workspace 5: 🖼 Images (`ChatImagesTabView`)
Dedicated browser for chat-bound images managed by NoxChat's Image Studio (`services/nox-image-tools/`):
- **Storage Header**: Displays total disk usage against the configured limit (`max_total_bytes`), total image count, and per-chat limits.
- **Per-Chat Image Explorer**: Chat list displaying per-chat image counts, disk size, and date ranges. Filters the gallery by chat or displays global assets across all chats.
- **Origin Filter**: Toggle between `generated` (AI Image Studio artwork) and `telegram` (user-uploaded chat photos).
- **Thumbnail Grid & Lightbox**: 256px lazy-loaded thumbnails displaying handle (`IMG-xxxxxxxx`), dimensions, file size, created date, and description. Clicking any image opens a 1024px preview lightbox with full prompt details.
- **Dry-Run Sweeps & Pruning**: 1-click deletion of checked images or age-based sweeps (`/sweepimages`). Prompts with a dry-run confirmation modal showing candidate counts and freed MB before pruning. Protects images currently referenced by active jobs.

### Workspace 6: ⚡ Active Jobs (`ActiveJobsTabView`)
Monitors in-flight and historical background sub-agent jobs:
- **Monitored Job Types**: `audio_stt` (Whisper transcription), `sound_classifier` (AST spectrogram detection), `song_recognize` (Shazam matching), `embedding` (vector batching), `sticker_map` / `sticker_reduce` / `sticker_pack_analysis` (sticker analyst), and `image_studio` (AI image generation & Pillow editing).
- **Job Statuses**: Tracks lifecycle states: `queued`, `running`, `completed`, `failed`, `cancelled`, and `interrupted`.
- **Operator Controls**: View execution duration, error logs, queued vs. active counters, and retry failed jobs.

### Workspace 7: 📜 Raw Logs & API (`RawLogsApiTabView`)
Comprehensive diagnostic logging, API payload interception, and chat trace inspection organized into three sub-tabs:

#### Sub-Tab 1: 📜 Bot Logs (Virtual Zstd)
- **Virtual Log View**: Embedded `VirtualLogViewControl` rendering monotonic log sequence numbers directly from zstd-compressed in-memory client cache.
- **WAL Spool Manager**: Toggle server-side 1GB WAL spooling (`config.yaml`).
- **Server-Side Retained Log Search**:
  - Full literal search powered by `noxchat-probe`'s `SearchLogs` RPC.
  - **Filters**: Case sensitivity toggle, UTC time range presets ("15 minutes", "1 hour", "24 hours", "All retained logs"), priority filter (0–7: Emergency to Debug), and unit filter.
  - **Snippet Highlighting**: Highlights matched substrings with exact UTF-16 character offsets and line previews.
  - **Jump to Sequence**: Clicking any search result instantly scrolls the virtual log viewer to that exact sequence number.
- **Spool Traversal & 1-Click Reset**: Forward chunk traversal ensures 100% of surviving spool records are retrieved across segment boundaries. A one-click `🗑️ Clear Spool` command atomically purges both server WAL files and client memory caches.

#### Sub-Tab 2: 🧠 Raw API Calls & Payloads
- **Lightweight Summary Timeline**: Requests summary metadata (IDs, models, timestamps, durations, token counts) while deferring full payload downloads until explicitly inspected.
- **Debounced Filter**: Responsive 75ms debounced filtering (`ApiFilterScope`) across call IDs, chat names, models, and timestamps.
- **Detail Panels**:
  - *CLI / Log-Style Parsed Chat Session*: Separates System Prompt sections (Identity, Voice, Protocols, Pinned Media, Recalled Memories, Hot Scratchpad) from User Messages and Bot Responses.
  - *Bidirectional (RTL / LTR) Multi-Language Typography*: Automatic base direction (`FlowDirection`) and alignment per message turn based on Unicode BiDi Rule P2/P3, using HarfBuzz cursive shaping for Persian, Arabic, and Hebrew.
  - *Raw JSON Panes*: Raw Request JSON and LLM Response JSON inspectors with copy-to-clipboard actions.
  - *Multimodal Image Gallery*: Attached multimodal thumbnails with MIME types and dimension badges.

#### Sub-Tab 3: 💬 Chats (Correlated Execution Traces)
- **Searchable Chat Sidebar**: Lists known chats by stable ID, type, and title.
- **Chronological Message Timeline**: Virtualized message history correlated directly with backend trace events.
- **Execution Trace Inspection**: Inspect exact API calls, RAG queries, tool invocations, and action dispatches triggered by each specific message turn.
- **Prompt Evolution**: Compares initial bootstrap system prompts with turn-by-turn step-revised prompt snapshots.
- **Authoritative Specification**: For complete protobuf schema, trace context propagation, and WAL recovery specifications, refer to [Chats Backend Specification](chats-backend-proposal.md).

---

## Security & Authentication

- **SSH-Style Tokens**: Cryptographically secure keys formatted as `noxchat-ed25519-v1:<base64url>`.
- **Key Generation**: Generate with `./bin/noxchat-probe -gen-key` or from the MADTOM Studio Settings dialog (`NoxSettingsModal`).
- **Server Deployment**: Operators paste the generated key into NoxChat's `.env` as:
  ```env
  NOXCHAT_CONTROL_KEY=noxchat-ed25519-v1:<token>
  ```
- **Transport Security**:
  - Mutual HMAC-SHA256 request signing with UTC timestamp and cryptographically random nonce (5-minute replay skew window).
  - Ephemeral self-signed TLS 1.3 encryption across network hops.
  - Local Python loopback strictly bound to `127.0.0.1:50056` (a warning is logged if bound to any external interface).

---

## Performance & In-Memory Caching

- Follows the MADTOM Telemetry `Journal Logs` memory architecture.
- **Dual Tier Caching**:
  1. *Log & API Chunk Cache*: Stores zstd-compressed blocks, decoding records on demand only for currently visible rows in the viewport.
  2. *Streamed State & Telemetry Cache*: In-memory numeric metric rings with LTTB downsampling.
- **Diagnostics & Stress Testing**:
  - Live Cache Size (MiB) and Decoded Chunk gauges.
  - `Force GC Collect` trigger for garbage collection benchmarking.
  - `Force Load (Stress Test)` generating simulated zstd chunk streams for memory pressure verification.
- **Zero Overhead When Disconnected**: If no MADTOM Studio client is connected, the Python backend disables all telemetry samplers and intercepts; the Go daemon flushes ring buffers to 0 bytes.

## Sandbox control RPCs

The Go probe exposes sandbox administration on `NoxChatControlService` in
[`noxchat.proto`](../../../MADTOM_GOLANG/proto/madtom/v1/noxchat.proto), forwarding
requests to the Python `sandbox_*` handlers using the same JSON bridge as Tips.
All request/response types use the `NoxSandbox` prefix. Every request includes
`node_id`; every response includes `error`, which clients should display when set.

| gRPC method | Python bridge method | Timeout |
|---|---|---|
| `ListSandboxVms` | `sandbox_list_vms` | 15s |
| `SandboxVmAction` | `sandbox_vm_action` | 60s |
| `ListSandboxFiles` | `sandbox_list_files` | 30s |
| `ReadSandboxText` | `sandbox_read_text` | 30s |
| `PreviewSandboxFile` | `sandbox_preview` | 60s |
| `ListSandboxBackups` | `sandbox_backups` | 15s |
| `RestoreSandboxBackup` | `sandbox_backups` (with `backup_id`) | 120s |
| `ListSandboxLibraryRequests` | `sandbox_library_requests` | 15s |
| `DecideSandboxLibrary` | `sandbox_decide_library` | 60s |
| `GetSandboxImageStatus` | `sandbox_image_status` | 15s |
| `RebuildSandboxImage` | `sandbox_rebuild_image` | 15s |
| `ListSandboxRuns` | `sandbox_runs` | 10s |

Inventory includes full VM details, sessions/jobs, chat titles, pools, totals and
host disk usage. Image status includes approved/pending pip and apt packages,
last build details and version usage counts. File previews expose `png_b64` as a
base64 string. Backup sizes and file sizes are 64-bit integers; timestamps remain
RFC3339 strings. Library rows preserve the database integer `notified` field.

Set optional `chat_id` explicitly for operations on chat `0`; omit it only to list
library requests or runs across all chats. Optional `depth` preserves explicit
zero; omission uses Python's default of 2. `approve=false` may be omitted by Go's
JSON encoding and is interpreted by Python as rejection. Runs use `limit` and
`offset`, returning `runs`, `total`, `limit` and `offset`.

### Sandbox message fields

All names below have the `NoxSandbox` prefix. `[]` means repeated and `?` means
optional presence. Request `node_id` is always field 1.

| Message | Fields (protobuf type followed by JSON key) |
|---|---|
| `Session` | `string name`, `bool busy`, `string started` |
| `Job` | `string job_id`, `string state`, `string started` |
| `VmInfo` | `int64 chat_id`, `string kind`, `string state`, `string image_version`, `int64 mem_mb`, `int32 vcpus`, `int64 pid`, `string since`, `string last_used`, `string idle_deadline`, `string session_deadline`, `int64 disk_used_bytes`, `int64 disk_quota_bytes`, `int64 snapshot_bytes`, `int32 backups`, `double cpu_seconds_today`, `double cpu_budget_seconds`, `[]Session sessions`, `[]Job jobs`, `string chat_title` |
| `Pool` | `int32 ready`, `int32 target` |
| `Totals` | `int32 running`, `int64 guest_mem_mb`, `int32 max_vms`, `int64 max_guest_mem_mb` |
| `Host` | `double state_dir_used_percent` |
| `FileEntry` | `string path`, `string type`, `int64 size`, `string mtime` |
| `Backup` | `string id`, `string created_at`, `int64 bytes` |
| `LibraryRequest` | `int64 id`, `int64 chat_id`, `?int64 requester_user_id`, `string manager`, `string package`, `string reason`, `string status`, `string decided_by`, `string decided_at`, `string decision_note`, `string image_version`, `int32 notified`, `string created_at` |
| `Packages` | `[]string pip`, `[]string apt` |
| `LastBuild` | `string version`, `bool ok`, `string finished`, `string log_tail` |
| `Run` | `int64 id`, `int64 chat_id`, `?int64 requester_user_id`, `string vm`, `string kind`, `string session`, `string code_preview`, `?int32 exit_code`, `string error`, `int64 duration_ms`, `int32 inputs`, `int32 exports`, `string background_job`, `string created_at` |
| `ListVmsRequest` | `string node_id` |
| `ListVmsResponse` | `[]VmInfo vms`, `map<string, Pool> pools`, `Totals totals`, `Host host`, `string error` |
| `VmActionRequest` | `string node_id`, `?int64 chat_id`, `string action`, `string session` |
| `VmActionResponse` | `bool ok`, `string image_version`, `string error` |
| `ListFilesRequest` | `string node_id`, `?int64 chat_id`, `string path`, `?int32 depth`, `bool refresh` |
| `ListFilesResponse` | `[]FileEntry entries`, `string as_of`, `bool from_cache`, `string error` |
| `ReadTextRequest` | `string node_id`, `?int64 chat_id`, `string path` |
| `ReadTextResponse` | `string text`, `bool truncated`, `bool binary`, `int64 size`, `string error` |
| `PreviewFileRequest` | `string node_id`, `?int64 chat_id`, `string path`, `int32 page` |
| `PreviewFileResponse` | `string png_b64`, `int32 width`, `int32 height`, `int32 pages`, `string kind`, `string error` |
| `ListBackupsRequest` | `string node_id`, `?int64 chat_id` |
| `ListBackupsResponse` | `[]Backup backups`, `string error` |
| `RestoreBackupRequest` | `string node_id`, `?int64 chat_id`, `string backup_id` |
| `RestoreBackupResponse` | `bool ok`, `string error` |
| `ListLibraryRequestsRequest` | `string node_id`, `?int64 chat_id`, `string status` |
| `ListLibraryRequestsResponse` | `[]LibraryRequest requests`, `string error` |
| `DecideLibraryRequest` | `string node_id`, `[]int64 ids`, `bool approve`, `string note`, `string decided_by` |
| `DecideLibraryResponse` | `bool ok`, `[]int64 ids`, `string error` |
| `ImageStatusRequest` | `string node_id` |
| `ImageStatusResponse` | `string current_version`, `bool building`, `string build_started`, `LastBuild last_build`, `Packages packages`, `Packages pending_packages`, `map<string, int32> versions_in_use`, `string error` |
| `RebuildImageRequest` | `string node_id` |
| `RebuildImageResponse` | `bool ok`, `bool building`, `string error` |
| `ListRunsRequest` | `string node_id`, `?int64 chat_id`, `int32 limit`, `int32 offset` |
| `ListRunsResponse` | `[]Run runs`, `int64 total`, `int32 limit`, `int32 offset`, `string error` |

### Sandbox tab (`SandboxTabView` / `SandboxTabViewModel`)

Registered in `MainView.axaml` right after Tips, the **📦 Sandbox** tab monitors and administers Nox's Firecracker sandbox VMs across four sub-tabs:
- **VMs**: Lists all sandbox VMs with All / Hot (`booting|warm|busy`) / Cold (`hibernated|cold`) filtering, running/memory totals, `temp` and `pre` pool counts, host state directory usage percentage, optional 10s auto-refresh, per-VM session/job details, and VM actions (`reset_session`, `hibernate`, `restart`, `kill`).
- **Files & Backups**: Lists `/data` files for the selected VM (or typed `chat_id`) with depth-2 expand-on-demand, cache indicators, a read-only monospace plaintext viewer (`ReadSandboxText`), a host-rendered PNG previewer (`PreviewSandboxFile`, capped at 16 MB `png_b64`, with multi-page navigation), and volume backup listing/restoration (`ListSandboxBackups`, `RestoreSandboxBackup`).
- **Libraries & Image**: Displays the `sandbox_library_requests` queue with status filtering and multi-select approval/rejection (`decided_by="studio"`), alongside guest rootfs image build status, installed/pending `pip` and `apt` packages, active image versions, and on-demand rebuilds (`RebuildSandboxImage`).
- **Runs**: Paged audit log (`ListSandboxRuns`, limit 50, offset paging, optional `chat_id` filter) displaying execution durations, exit codes, single-line code previews (with full code tooltip), input/export counts, and background job IDs.

## Automation tab (`AutomationTabView` / `AutomationTabViewModel`)

Registered in `MainView.axaml` right after the Sandbox tab, the **🤖 Automation** tab monitors and manages NoxChat's per-chat preprocessors and scheduled crons via `GetAutomationStatus`, `ListPreprocessors`, `GetPreprocessor`, `PreprocessorAction`, `ListPreprocessorLogs`, `ListCrons`, and `CronAction`:
- **Per-Chat Status Grid**: Displays per-chat preprocessor counts (`enabled / total`), active crons, hourly run/say counters, daily cron fires, next fire timestamp, and consecutive `say` streak (highlighted when `SayStreak >= 4`), with optional `chat_id` filtering and a collapsible **Effective config** JSON viewer.
- **Preprocessors**: Lists per-chat `code` and `static` preprocessors with triggers (`message`, `cron`), run counts, and error tracking. Selecting a row loads its verbatim Python code, persisted state JSON, and static actions JSON into read-only monospace text boxes (no execution or HTML rendering), with controls to `enable`, `disable`, `reset_errors`, `reset_state` (with confirmation), and `delete` (with confirmation).
- **Crons**: Lists scheduled crons (`at`, `every`, `cron` specs, next fire times, fire counts, recursion depth) with `enable`, `disable`, and `cancel` (with confirmation) actions.
- **Logs**: Paged execution log viewer (`ListPreprocessorLogs`, page size 50) showing event kind, outcome (`pass`, `drop`, `stop`, `say`, `invoke`, `cron`, `error`, `skipped`, `blocked`, `immune`), detail, duration, and a toggle to filter logs to the selected preprocessor.


