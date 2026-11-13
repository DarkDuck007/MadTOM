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
