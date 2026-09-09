# MADTOM Telemetry: Daemon, Collector & Display Architecture

## 1. Executive Summary & Philosophy

This architecture defines a lightweight, resilient, and decoupled 3-tier telemetry pipeline tailored for MADTOM:

```mermaid
flowchart LR
    subgraph Tier1["1. Monitored Devices"]
        D1["madtom-daemon<br/>(Linux/Windows)"]
        WAL["Local Disk Spool<br/>(WAL Buffer)"]
        D1 <--> WAL
    end

    subgraph Tier2["2. Collector Engine"]
        COL["madtom-collector<br/>(Go Engine)"]
        TSDB[("Storage & Downsampling<br/>(Local TSDB)")]
        COL <--> TSDB
    end

    subgraph Tier3["3. Display & Control"]
        UI["MADTOM Telemetry<br/>(Avalonia Desktop / Console)"]
    end

    D1 -- "Push Stream OR Pull Poll<br/>(Flushes on Connect)" --> COL
    COL -- "LOD-Downsampled Queries<br/>+ 1Hz Live Stream (IPC/gRPC)" --> UI
```

### Core Tenets
1. **Extreme Edge Resilience**: The monitored device is completely autonomous. If the collector is offline, down, or unreachable, metrics are committed to a bounded local disk spool. When connectivity resumes, the backlog flushes in order, then purges locally.
2. **Dual-Mode Collector (Push vs. Pull)**:
   * **Push**: Daemons establish outbound connections to the collector and stream/batch updates.
   * **Pull**: Collector periodically scrapes daemons. Between scrapes, daemons spool to disk and dump their backlog on the next poll.
3. **Data Ownership Handoff**: Once the collector acknowledges receipt of a batch, ownership transfers. The monitored daemon frees local disk space immediately.
4. **Resolution-Adaptive Queries (LOD Rendering)**: Querying 60 days of data never transfers millions of raw 1-second ticks. The collector downsamples to match the display chart's pixel budget (e.g. 1,200 points + 2x zoom headroom). Zooming triggers sub-range high-resolution fetches on the fly, identical to how PDF renderers tile high-DPI viewports.
5. **Local IPC by Default**: Components 2 (Collector) and 3 (UI) frequently live on the same physical operator workstation, talking via high-throughput localhost IPC (Unix Domain Socket or Windows Named Pipe).

---

## 2. Component 1: Monitored Node Daemon (`madtom-daemon`)

The daemon is a single Go binary running as a system service (`systemd` / Windows Service).

```mermaid
flowchart TD
    subgraph Collectors["Collectors Subsystem"]
        CPU["CPU Collector<br/>• Overall & per-core matrix<br/>• User/System/IOwait<br/>• Scaling governor & freq"]
        MEM["Memory Collector<br/>• Total, Used, Available<br/>• Cached, Buffers, Dirty<br/>• Swap & ZRAM compression ratio<br/>• Page faults/sec"]
        PWR["Power & Battery Collector<br/>• Battery %, Health, Cycles<br/>• Charge/Discharge Watts<br/>• AC state & UPS status"]
        NET["Network Collector<br/>• Per-NIC rx/tx bytes & packets<br/>• Drops, errors, collisions<br/>• Link speed, MTU, carrier"]
    end

    SCHED["Tiered Collector Scheduler<br/>(1s fast / 10s normal / 60s slow)"] --> Collectors
    Collectors --> BATCH["Batch Assembler"]
    
    BATCH --> CHECK{"Collector Reachable?"}
    CHECK -- "Yes" --> SEND["Direct Stream / Push"]
    CHECK -- "No (or Pull Mode wait)" --> WAL["Local Disk Spool (WAL)<br/>• Append-only binary log<br/>• Bounded (e.g., max 250 MB)<br/>• Ring discard on overflow"]
    
    CONN["Collector Connected / Scraped"] --> FLUSH["Drain WAL & Stream Backlog"]
    FLUSH --> ACK{"Collector ACK?"}
    ACK -- "Yes" --> TRUNC["Truncate Spool Files"]
```

### Detailed Collector Metrics Specification

| Subsystem | Specific Metrics Captured | Collection Cadence |
| :--- | :--- | :--- |
| **Memory** | `MemTotal`, `MemFree`, `MemAvailable`, `Buffers`, `Cached`, `Dirty`, `Writeback`, `AnonPages`, `Mapped`, `SwapTotal`, `SwapFree`, `ZramOrigSize`, `ZramComprSize`, `ZramRatio`, `pgfault/s`, `pgmajfault/s` | Normal (5–10s) |
| **CPU** | Total utilization %, per-core utilization array (for detailed core matrix), `user`, `system`, `iowait`, `steal`, `idle`, load averages (1/5/15m), context switches/s, core frequencies | Fast (1s) |
| **Power & Battery** | AC plugged (`bool`), Battery present (`bool`), state (`Charging`, `Discharging`, `Full`), percentage, current energy (`Wh`), design capacity (`Wh`), rate (`Watts`), cycles, health % | Slow (15–30s) |
| **NICs** | Per-interface: `rx_bytes`, `tx_bytes`, `rx_packets`, `tx_packets`, `rx_dropped`, `tx_dropped`, `rx_errors`, `tx_errors`, link speed (`Mbps`), carrier (`up/down`), MTU | Fast (1s) |

### Local Disk Spooling (WAL Engine)
* **Storage Location**: `/var/spool/madtom/wal/` (Linux) or `%ProgramData%\MADTOM\spool\` (Windows).
* **Segmented Binary Files**: Rotates files every 10 MB or 10 minutes (`segment-00001.wal`).
* **Strict Bounded Quota**: Configurable hard limit (default `250 MB`). When the volume is exhausted while offline, the oldest segment is purged (FIFO) to protect host storage.
* **Drain Protocol**: When a connection to the collector succeeds, the daemon initiates a draining stream from the oldest unacknowledged segment offset.

---

## 3. Component 2: Information Collector (`madtom-collector`)

The collector is the ingestion, buffering, downsampling, and query coordinator.

```mermaid
flowchart TD
    subgraph Ingestion["Ingestion Interface (Dual-Mode)"]
        MODE_A["Mode A: Push Listener<br/>(gRPC Server listening for Daemons)"]
        MODE_B["Mode B: Pull Scraper<br/>(Scheduled dialer fetching Daemon backlogs)"]
    end

    DAEMON["Monitored Daemons"] --> MODE_A
    MODE_B --> DAEMON

    MODE_A --> INGEST_BUF["Ingestion Ring Buffer"]
    MODE_B --> INGEST_BUF

    INGEST_BUF --> STORAGE[("Local Time-Series Store<br/>(Pebble / DuckDB / Embedded TSDB)")]
    
    subgraph QueryEngine["Query & Downsampling Engine"]
        LOD["Resolution Downsampler<br/>(LTTB / Bucket Aggregation)"]
        CACHE["High-Resolution Tile Cache"]
        LIVE_FAN["1Hz Live Streaming Bus"]
    end

    STORAGE --> LOD
    INGEST_BUF --> LIVE_FAN
    LOD --> CACHE

    UI["MADTOM Telemetry UI"] <-->|"gRPC over IPC / Localhost"| QueryEngine
```

### Ingestion: Mode A (Push) vs. Mode B (Pull)

#### Mode A: Push (Daemon-to-Collector Stream)
```mermaid
sequenceDiagram
    autonumber
    participant Daemon as madtom-daemon (Node)
    participant Spool as Local WAL (Disk)
    participant Collector as madtom-collector

    Note over Daemon,Collector: Collector is offline or unreachable
    Daemon->>Spool: Collector down: Append telemetry to segment-001.wal
    Daemon->>Spool: Append telemetry to segment-002.wal

    Note over Daemon,Collector: Collector comes back online
    Daemon->>Collector: Dial gRPC Connect(HostIdentity)
    Collector-->>Daemon: Handshake OK (LastSyncedOffset: 0)
    
    loop Flush Offline Backlog
        Daemon->>Spool: Read un-ACKed segment chunks
        Daemon->>Collector: PushBatchStream(records, isBacklog=true)
        Collector-->>Daemon: BatchAck(segmentId, offset)
        Daemon->>Spool: Truncate acknowledged segment
    end

    Note over Daemon,Collector: Normal live streaming resumes
    Daemon->>Collector: PushBatchStream(realtime_1s_records)
    Collector-->>Daemon: BatchAck()
```

#### Mode B: Pull (Collector-Scheduled Scrape)
```mermaid
sequenceDiagram
    autonumber
    participant Collector as madtom-collector
    participant Daemon as madtom-daemon (Node)
    participant Spool as Local WAL (Disk)

    Note over Daemon: Daemon samples 1s metrics locally
    Daemon->>Spool: Write ongoing metrics to local WAL

    Note over Collector,Daemon: Collector wakes up (e.g. every 15s or 60s)
    Collector->>Daemon: gRPC PollTelemetry(SinceOffset)
    Daemon->>Spool: Read all pending samples since offset
    Daemon-->>Collector: TelemetryPayload(samples, endOffset)
    Collector-->>Collector: Commit to Local Store
    Collector-->>Daemon: AckOffset(endOffset)
    Daemon->>Spool: Purge committed WAL records
```

---

## 4. Component 3: Display Device & Resolution-Adaptive LOD Querying

The UI communicates with the collector using two primitives:
1. **Live Feed**: A continuous streaming subscription for active graphs (60 samples @ 1s resolution).
2. **Historical Range Query with Resolution Budget**: Used for overview cards, deep-dive timeline inspection, and historical scopes (1h, 24h, 7d, 60d, custom).

### The Resolution-Adaptive Downsampling Pipeline (LOD)

```mermaid
flowchart LR
    subgraph UI_Viewport["UI Chart Viewport"]
        VIEW["Screen Width: 1200 Pixels<br/>Requested Range: 60 Days"]
    end

    subgraph Collector_Downsampler["Collector LOD Engine"]
        REQ["Request: Range[T-60d, T]<br/>TargetPoints: 1200<br/>Headroom: 3x (3600 pts)"]
        FETCH["Scan Raw Range from TSDB<br/>(5,184,000 raw samples)"]
        BUCKET["Downsampler (LTTB / Bucket MinMaxAvg)<br/>Step = 60d / 3600 = ~24 mins"]
        OUT["Return 3,600 Downsampled Points<br/>(Smooth initial rendering + 3x Zoom)"]
    end

    VIEW --> REQ
    REQ --> FETCH --> BUCKET --> OUT --> VIEW
```

### On-Demand Sub-Range Zooming (PDF-Style Tiling)

```mermaid
sequenceDiagram
    autonumber
    participant UI as MADTOM Telemetry UI
    participant Collector as madtom-collector

    UI->>Collector: QueryRange(Start: -60d, End: now, TargetResolution: 1200)
    Note over Collector: 5,184,000 raw points downsampled to 3,600 points
    Collector-->>UI: Points[3600] (Covers full range with 3x zoom headroom)

    Note over UI: User zooms 10x into Day 14 (12:00 to 14:00)
    UI->>Collector: QueryRange(Start: Day14-12:00, End: Day14-14:00, TargetResolution: 1200)
    Note over Collector: Fetches only 7,200 raw points in 2-hour window<br/>Downsamples to 1,200 points
    Collector-->>UI: High-Resolution Tile Data Points[1200]
    Note over UI: Re-renders zoom window at full native resolution
```

---

## 5. Deployment Topology: Co-Located vs. Distributed

```mermaid
flowchart TD
    subgraph Standard_Workstation["Scenario A: Operator Workstation (Co-located 2 & 3)"]
        UI_A["MADTOM Console / UI"] <-->|"IPC Socket / Named Pipe<br/>(Zero network overhead)"| COL_A["madtom-collector"]
    end

    subgraph Remote_Nodes["Remote Hosts"]
        N1["Server Node 1<br/>(madtom-daemon)"] -->|"mTLS / WAN"| COL_A
        N2["Server Node 2<br/>(madtom-daemon)"] -->|"mTLS / LAN"| COL_A
        N3["Edge Gateway<br/>(madtom-daemon)"] -->|"mTLS / WireGuard"| COL_A
    end

    subgraph Dedicated_NOC["Scenario B: Central Fleet Operations"]
        COL_B["Central madtom-collector<br/>(Headless on Server)"]
        UI_B1["Engineer Laptop 1 (UI)"] <-->|"Remote gRPC"| COL_B
        UI_B2["NOC Wall Display (UI)"] <-->|"Remote gRPC"| COL_B
    end
```

---

## 6. Stage-1 Implementation Roadmap (Minimal Viable Scope)

To keep the initial phase compact, functional, and verifiable, we break Stage 1 into 4 sequential steps:

```mermaid
gantt
    title Stage-1 Minimal Roadmap
    dateFormat  X
    axisFormat %d
    
    section Step 1: Protocol & Contracts
    Protobuf definitions (Metrics, Batch, SpoolHeader) :0, 2
    
    section Step 2: Monitored Node Daemon
    Linux collectors (CPU matrix, Mem+Swap/ZRAM, Power, NICs) :2, 5
    Local WAL spooler (file write, drain, purge) :4, 7
    
    section Step 3: Information Collector
    Push/Pull ingestion listener :6, 9
    Embedded disk storage & LTTB downsampler :8, 11
    
    section Step 4: UI IPC & Client
    C# Local IPC provider in MADTOM Telemetry :10, 13
    Live 1Hz streaming + adaptive range zoom verification :12, 15
```

### Deliverables by Step:

1. **Step 1: Protocol Core (`proto/madtom/v1/`)**:
   * `metrics.proto`: Memory (including swap/zram), CPU (with per-core array), Power/Battery, NIC counters.
   * `ingest.proto`: `PushBatchStream(stream Batch) returns (Ack)`, `PollTelemetry(Request) returns (Batch)`.
   * `query.proto`: `QueryRange(time_range, resolution_points) returns (SeriesData)`, `SubscribeLive(interval) returns (stream LiveSnapshot)`.

2. **Step 2: Linux Node Daemon (`agent/`)**:
   * Pure Go Linux `/proc` and `/sys` collectors (zero external dependencies).
   * Bounded directory WAL appending binary chunk files.
   * Auto-drain on connection with server ACK validation.

3. **Step 3: Information Collector (`collector/`)**:
   * Standalone Go executable (`madtom-collector`).
   * Embedded storage (Pebble key-value store or SQLite time-series tables).
   * Ingestion router supporting both Push listener and Pull scraper.
   * LTTB (Largest-Triangle-Three-Buckets) downsampling engine.

4. **Step 4: C# Telemetry Client Integration (`src/Plugins/Telemetry/`)**:
   * `CollectorIpcDataProvider`: Connects to local Unix domain socket (`/run/madtom/collector.sock`) or TCP endpoint.
   * Feeds live data into `DetailedCoreMatrixControl`, `TwampTimeSeriesChartControl`, and sparklines.
   * Connects historical time range selectors (10s, 30s, 1m, 5m, custom) to the collector's resolution-adaptive LOD query endpoint.

