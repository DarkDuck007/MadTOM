# MADTOM: NOX AI Control Center Plugin

The **NOX AI Control Center** (`MADTOM.Plugins.NoxAI`) provides a real-time, authenticated, and encrypted control and telemetry workspace dedicated to **NoxChat** (the autonomous multimodal Telegram bot and memory engine in the submodule `NoxChat/`).

---

## Architecture Overview

```
┌────────────────────────────────────────────────────────┐
│               MADTOM Studio (Avalonia C#)              │
│  - NoxChat Control Workspace Tabs                      │
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
│  - Drop-oldest FIFO eviction on slow networks          │
│  - Zero-leak disconnect cleanup                        │
│  - Server-side WAL Spool (1GB default disk quota)      │
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

## Key Features & Workspaces

### 1. Direct Chat (`DirectChatTabView`)
- Bypasses Telegram directly into the NoxChat processing pipeline.
- Interacts as authenticated operator user `"MADTOM"` with user ID `"Control Center"`.
- Live streamed thought process and tag-event updates as the bot reasons, searches memories, and invokes tools.

### 2. Memories & RAG Explorer (`MemoriesRagTabView`)
- **Hot Memory Scratchpad**: Real-time view and editing of active working memory.
- **Atomic Memories**: Search, filter, and inspect long-term atomic memories stored in SQLite and Qdrant.
- **Hybrid & Semantic Search**: Search long-term memories using hybrid dense embedding vectors and BM25 sparse rankings with real-time score badges.
- **Vector Point Inspection**: Inspect embedded vectors, cosine distances, payloads, and underlying message context.

### 3. Media & Sticker Library (`MediaLibraryTabView`)
- Visual grid of indexed Telegram stickers, GIFs, and media packs.
- **Dual-Mode Search**: Real-time text-based search (matching sticker set name, `t.me/addstickers/` set URL, or sticker caption/definition/emoji) combined with Enter-triggered semantic embedding search via `MediaVectorStore` / Qdrant.
- Inspect and modify semantic tag annotations and pinned favorite reactions.
- Trigger manual background analysis and embedding jobs.

### 4. Active Background Jobs (`ActiveJobsTabView`)
- Monitor asynchronous tasks:
  - Audio STT (Whisper transcription & pipeline).
  - AST (Audio Spectrogram Transformer sound detection).
  - Shazam audio fingerprint identification.
  - Text & image vector embeddings.
- Track progress, duration, queued vs. active counts, and retry failed jobs.

### 5. Raw Logs & API Interceptor (`RawLogsApiTabView`)
- **Virtual Log View**: Embedded `VirtualLogViewControl` rendering monotonic log sequence numbers directly from zstd-compressed in-memory client cache.
- **WAL Spool Manager**: Toggle server-side 1GB WAL spooling (`config.yaml`).
- **Spool Retrieval & Clean Reset**: Dynamic forward chunk traversal guarantees 100% of surviving spool records are fetched even after quota-based segment rotation. A one-click `🗑️ Clear Spool` command triggers an atomic server-side WAL and memory ring reset along with client cache clearing. Full historical log/spool preloading is safely hosted in the Settings *Performance & Caches* tab to prevent UI stalls.
- **Lightweight API Calls List & On-Demand Detail Loading**: Timeline loads summary-only metadata (IDs, models, timestamps, durations, token counts) in fractions of a second without transferring heavy JSON payloads or images. When an API call is selected, full request/response JSONs, parsed sessions, and image galleries are fetched and cached on-demand via `GetApiCallDetail`.
- **CLI / Log-Style Parsed Chat Session**: Dedicated parser tab clearly distinguishing:
  1. *System Prompt & Structured Sections*: Separates Identity & Role, Voice & Style, Technical Perspectives, Communication Protocol, Pinned Media, Recalled Memories, Hot Scratchpad, Background Tasks, and Temporal Context.
  2. *Users' Messages*: Distinct styling with user headers, timestamps, and message text.
  3. *Bot's Responses*: Model latency, completion tokens, extracted `<thought>` reasoning boxes, tool executions, and final reply.
  4. *Attached Multimodal Media*: Integrated preview cards showing thumbnails, MIME types, and dimensions.
  5. *Bidirectional (RTL / LTR) Multi-Language Typography*: Automatic base direction (`FlowDirection`) and alignment detection per message turn and system section based on Unicode BiDi Rule P2/P3. Employs natural HarfBuzz cursive shaping for Persian, Arabic, and Hebrew without fixed-width monospace cell conflicts.

### 6. Usage & Python Runtime Telemetry (`NoxTelemetryTabView`)
- Real-time 1 Hz stream of internal Python runtime state from the process's point of view:
  - Exact Resident Set Size (RSS) and Virtual Memory Size (VMS) via `/proc/self/statm`.
  - Python CPU utilization percentage across all threads.
  - Active `asyncio.all_tasks()` count and event-loop lag monitoring.
  - Python generational GC collection counts (`gc.get_count()`).
  - LLM token throughput: prompt tokens, completion tokens, cached tokens.
- **Dynamic Timeframe Selection**: Switchable time horizons (`5M`, `15M`, `1H`, `6H`, `24H`, `7D`) that automatically query historical telemetry ranges and adjust nanosecond chart boundaries.
- Interactive multi-series visualization powered by `MetricHistoryChartControl` with LTTB downsampling.

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
  - Local Python loopback strictly bound to `127.0.0.1` (a warning is logged if bound to any external interface).

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
