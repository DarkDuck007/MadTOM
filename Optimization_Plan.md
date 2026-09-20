# MADTOM Resource Optimization Plan

Research date: 2026-09-15. Scope: Go daemon, collector, WAL, gRPC transport, Pebble storage, and .NET/Avalonia telemetry UI.

## Implementation progress — 2026-09-16

The research findings below remain a dated baseline. Two portions have now been implemented after rereading the current sources:

1. **Pull freshness:** ingestion returns a compact sample count/latest-timestamp summary internally. Pull catch-up uses it for raw and compressed batches, stops on empty results, and keeps acknowledgement advancement after successful ingestion. Regression cases cover compressed/raw backlog, unordered timestamps, empty batches, and corrupt compressed data. The ingestion package tests and documentation link checks passed at this stage.
2. **Live delivery:** each subscription keeps one pending snapshot, replacing stale pending state; unsubscribe releases the channel reference and removes empty subscriber entries. Tests cover slow/fast subscribers, preserved historical values, cached state, timestamp monotonicity, and concurrent subscription churn. The ingestion race tests and documentation link checks passed at this stage.

Final verification: `go test -race -timeout 120s ./...` passed across the Go backend. The architecture guide documents both behaviors. No WAL format, durability policy, protobuf schema, UI code, or codec defaults were changed. Byte/decode limits, empty-spool daemon sampling ownership, collection scheduling, query bounds, and compression tuning remain future work. No end-to-end performance gains have been benchmarked yet.

### Client history cache follow-up

Implemented a provider-owned, in-memory numeric history cache before further zstd work. Default retention is one hour, configurable up to 24 hours through **Node Settings → Collectors**, with an observed-rate memory estimate and a clear-cache button. The retention preference persists separately in `telemetry-cache.json`; telemetry remains session-only. Local monitor-only history survives detail-page recreation, and stored-history queries reuse covered live windows or short-lived collector responses. Incoming numeric snapshots now remove omitted metrics before caching so stale values are not recorded again.

Validation: cache-core tests passed at the first stage; the integrated settings/navigation stage passed all 152 .NET tests, including tests for reopened graphs, retention persistence, invalid settings, clearing, collector identity, and in-flight query invalidation. Documentation link validation passed with 110 internal links. Full details are in the [Client History Cache guide](Documentation/ui-and-config.md#client-history-cache). No compression changes were made.

### Cached graph resolution follow-up

Metric graphs now request three times their plotting width in points and apply the same time-based display reduction to merged collector/cache history and live updates. Single-node graphs preserve sub-second timestamps. Sampling retains bucket endpoints and extrema, leaves the raw session cache unchanged, and uses retained source windows to avoid cumulative live-update reduction. Resizing refreshes the query budget; remote cache reuse checks resolution as well as time coverage.

Validation: all 160 .NET tests passed, including graph width/resize budgets, unreduced 30-minute 1 Hz history, spike retention, higher-resolution remote cache misses, and cached sub-second history displayed through the metrics view model. See [Resolution-Adaptive Downsampling](Documentation/ui-and-config.md#resolution-adaptive-downsampling). No compression changes were made.

### Drawing resolution follow-up

Separated the retained 3× history budget from actual chart geometry. **Node Settings → Performance** now exposes a persistent client-wide drawing density from 0.1× to 2× plotting width, defaulting to 1×. The renderer samples the zoomed visible window, retaining extrema and edge neighbours within its budget. Applying settings invalidates open chart geometry without changing history or requesting collector data.

Validation: all 166 .NET tests passed, including drawing budgets at 0.1×/1×/2×, extrema retention, visible-window edge continuity, sparse data, and persisted/invalid settings. Compilation passed using single-process MSBuild; test execution required local socket access outside the sandbox. All 111 internal documentation links passed validation.

### Performance controls follow-up

Drawing and retained-history density now each have a slider paired with a compact numeric up/down. Drawing remains 0.1×–2× (default 1×); retained metric history is configurable from 1×–10× (default 3×). Both preferences persist together, with backward-compatible defaults. History changes update open metric graphs through the existing debounced resolution refresh; drawing-only changes remain independent of history queries and raw cache retention.

Validation: all 170 .NET tests passed, covering configurable history budgets, both numeric values through the settings command, persistence, invalid values, and legacy settings defaults. XAML compilation and all 111 internal documentation links passed.

### Numeric editor layout correction

Performance numeric editors now use a 32-pixel height and a 22-pixel-wide column of stacked spin buttons, leaving space for the value. A scoped spinner template preserves native increment/decrement handling.

Validation: all 171 .NET tests passed, including a compiled-template regression test for vertically stacked buttons and native increase/decrease events. XAML compilation and all 111 documentation links passed.

### Scrolling stability correction

Anchored client history and drawing buckets to absolute timestamps at fixed scope/resolution. Sliding windows no longer continually regroup old interior samples; drawing retains original sample coordinates and values. Partial edge buckets remain free to update. Duplicate/late single-node notifications no longer overwrite samples, and repeated timestamps cannot reset a calculated counter rate.

Validation: all 174 .NET tests passed. Regression coverage advances a 30-minute window through ten updates at history 1× and drawing 1×/0.2×, verifying unchanged interior selections and original point coordinates/values. Duplicate/late rate updates and tiny viewport limits are covered. All 111 documentation links passed.

### Cache budgets and visibility follow-up

Moved cache controls from Collectors into a dense Performance grid. Added independent streamed/stored size budgets (default 64/32 MiB), stored age configuration (default 30 seconds), separate clears, usage/counts, forecasts/caps, and combined totals. Stats refresh every two seconds while settings are open. Size pressure evicts oldest streamed samples with queue-capacity reclamation and least-recently-used stored ranges. Settings persist with defaults for older files and invalidate in-flight fills. Estimates cover cache storage, excluding chart/transient arrays and runtime overhead.

Validation: build/XAML compilation passed. The full .NET run passed 178 of 179 tests; the daemon/collector integration test initially received a sample without process availability and passed its isolated retry. Cache regressions cover size eviction, allocated-buffer reclamation, immediate shrink, stored LRU/expiry, remote-only accounting, independent clears, in-flight invalidation, settings persistence, and legacy defaults. All 111 documentation links passed.

### Historical query bounds — 2026-09-17

Replaced the range RPC's unbounded raw materialization plus LTTB pass with a storage scan retaining bounded absolute-time endpoint/extrema buckets. Added four-scan collector admission, a ten-second deadline, five-million-record scan limit, and cancellation. The desktop now coordinates history RPCs with per-collector/global concurrency limits (4/8), identical-request sharing, and reference-counted cancellation. This changes range-query sampling from LTTB; sparse data and original sample timestamps/values are preserved. Legacy internal raw queries remain unchanged.

Validation: collector storage/API tests passed under the race detector; client coordinator tests cover shared readers, last-reader cancellation, queue cancellation, retries, and concurrency. Final verification passed all 182 .NET tests and the full Go race suite. In the local 100,000-point fixture, raw queries allocated 8,948,701 bytes/op (49 allocations) versus 25,112 bytes/op (12 allocations) for a 750-point bounded query. This microbenchmark measures allocations per query, not peak RSS or production latency; the scan still reads matching records up to its work limit.

### Collection work reduction — 2026-09-17

Process snapshots now maintain a 1,000-candidate heap, rank by CPU/RSS with deterministic PID ties, and defer UID/status reads and protobuf materialization until after selection. All process counters are still scanned for ranking/baselines; the pre-existing 1,000-process coverage limit remains. Swap and zram device probes are independently gated; all-OFF network override maps no longer force collection. WAL segmentation remains unchanged because current acknowledgements delete whole segments; grouping records requires record-level replay/acknowledgement work first.

Validation: a 1,050-process fixture verifies all 1,050 counter baselines are retained while only 1,000 status files are read and the correct leaders are returned. Separate probe tests cover swap/zram combinations and all-OFF network overrides. Collector tests and the full Go race suite passed; all 111 internal documentation links passed. Further metadata scheduling, TWAMP isolation, and compression byte/decode safeguards remain planned.

### Grouped WAL and durable replay cursors — 2026-09-17

Records now share segments up to 10 MiB (bounded further by the spool quota), retaining per-record fsync. Existing wire offsets now drive durable prefix acknowledgements in push, pull, and reverse-push; `wal-state.json` atomically checkpoints cursors and sequence high-water state before deletion. Replay stops at record boundaries and a 3 MiB byte budget, and startup repairs only incomplete newest-file tails. Legacy records remain readable, with explicit errors for oversized or corrupt data. Pull empty-spool sampling returns the pending prefix to avoid acknowledging past a concurrent sampler record. Sampling errors are surfaced. WAL zstd decode is bounded at 64 MiB; compression scope is unchanged.

Validation: `go test -race -timeout 90s ./...` passed across the backend, including push/pull/reverse-push integration. WAL tests cover lost and duplicate ACKs, appends during an in-flight batch, partial/range ACKs, restart cursor recovery, torn latest/sealed frames, corrupt complete records, failed checkpoint persistence, sequence high-water recovery, legacy/compressed replay, byte limits, atomic sample records, and quota behavior. A 100-write fixture using this implementation created 100 segments with forced per-record rotation versus one grouped segment; every append retained its fsync. This demonstrates file-count reduction, not a production throughput or hardware power-loss guarantee. All 111 documentation links passed.

## 1. Recommendation

**Use zstd selectively for serialized, sufficiently large, relatively cold data. First bound resource growth and eliminate unnecessary collection, copying, serialization, and disk operations.** These changes address costs that compression cannot remove.

The proposed order is:

1. Establish resource budgets and measurements; fix compression-dependent pull behavior and enforce byte limits.
2. Skip unnecessary probes, bound UI histories and pending updates, and make historical queries use bounded memory.
3. Reduce WAL file churn while preserving durable acknowledgements; avoid decoding and re-encoding unchanged payloads.
4. Benchmark tuned zstd for WAL/ingest and collector-to-UI responses; benchmark Pebble compression separately.
5. Add retention and persistent rollups; consider compact time-series chunks and cold compressed caches if still needed.

This is a research and implementation plan, not a performance benchmark. Expected benefits below are hypotheses grounded in source inspection. No builds, tests, benchmarks, database opens, dependency installations, or running-service changes were performed. Existing workspace files were treated as read-only; this document is the only intended write. The workspace was already being modified, including the WAL, transport, opt-in logic, protocols, and merge utility. Findings describe files as read during this review, not an atomic revision; recheck named functions before implementation.

### Why “zstd across everything” needs refinement

A `List<T>`, object graph, map, or observable collection cannot remain directly usable in its ordinary form while its contents are zstd-compressed. It must become serialized bytes and be decoded before normal access. Keeping both representations can increase memory. Repeatedly compressing an actively updated chart adds CPU and temporary buffers to a path that should stay cheap.

Use three explicit representations:

| Data temperature | Representation | Examples |
|---|---|---|
| Hot: read or changed continuously | Bounded typed arrays/ring buffers, immutable snapshots | Latest node state, active chart windows, process delta state |
| Warm: reused sometimes | Small decoded cache with a byte budget | Recently viewed history chunks |
| Cold: rarely accessed | Indexed, independently compressed blocks | Long history cache, WAL records, archives |

For a cold cache, approximate steady memory as `compressed bytes + index + decoded-cache budget + codec workspaces`. Peak memory also includes raw serialization, compressed output, decoded output, and materialized objects that coexist. Compression only helps if these totals beat the uncompressed alternative at acceptable latency.

## 2. Findings in the current code

Paths below are relative to the workspace. Function names are stable search anchors; line numbers are intentionally omitted because development is active.

| Finding and evidence | Resource consequence | Priority |
|---|---|---|
| [wal.go](MADTOM_GOLANG/pkg/daemon/spool/wal.go), `WriteMetrics`: unconditionally rotates, writes, and syncs each batch. Both sampling loops normally submit one sample. `maxSegmentSize` does not drive rotation. | At a nominal 1 Hz, an uninterrupted offline day can create about 86,400 files before quota eviction. Small compressed payloads still consume filesystem allocation units, directory entries, and inodes. | P1 |
| Same file, `ReadBatchChunk` / `readSegmentFileLocked`: reads, unmarshals, decompresses, aggregates, marshals, and possibly recompresses under the WAL mutex. | Replay competes with sample writes; multiple byte buffers and object graphs coexist. Even normal push delivery rereads the spool. | P1 |
| Same file: compression on append has no size/savings threshold; replay compresses only when sample count exceeds five. | Enabling `-zstd` does not consistently compress low-volume streamed batches. Sample count is a poor predictor of payload size. | P0/P1 |
| [pull_scraper.go](MADTOM_GOLANG/pkg/collector/ingest/pull_scraper.go), `executeScrape`: freshness termination reads `batch.Samples`. [pipeline.go](MADTOM_GOLANG/pkg/collector/ingest/pipeline.go) decodes compressed samples into a local variable without populating that field. | Compressed batches bypass this freshness condition and can cause extra drain calls. After backlog empties, `PollTelemetry` can collect another sample, adding work beyond the background sampler. This is a source-derived scenario, not a reproduced benchmark. | P0 |
| [push_client.go](MADTOM_GOLANG/pkg/daemon/transport/push_client.go), `sampleLoop`, and [pull_server.go](MADTOM_GOLANG/pkg/daemon/transport/pull_server.go), `Start`: call `Collect` before stripping offline monitor-only data. | Offline process and subsystem collection still consumes CPU/syscalls even when its output is discarded. | P1 |
| [collector.go](MADTOM_GOLANG/pkg/daemon/collector/collector.go), `Collect`: runs enabled subsystems together; fast interval drives sampling. Normal/slow intervals exist in config but are not scheduled in these paths. TWAMP runs synchronously while the engine lock is held. | Slow metadata probes and network timeouts delay all collection. The loop waits after work, so work duration adds cadence drift. | P1 |
| [process_linux.go](MADTOM_GOLANG/pkg/daemon/collector/process_linux.go), `Collect`: scans processes, reads stat/status, builds all results, sorts, then truncates to 1,000. | Output is bounded, but scan work, temporary objects, sorting, and previous-PID state still scale with process count. Slicing does not shrink the backing array. | P1 |
| [tsdb.go](MADTOM_GOLANG/pkg/collector/storage/tsdb.go), `QueryRange`: collects all matching points into a slice; [query_server.go](MADTOM_GOLANG/pkg/collector/api/query_server.go) applies LTTB afterward. | A 1,200-point response can require millions of raw points in memory. The response point limit is not a scan or memory limit. Storage scanning does not take the request context. | P0/P1 |
| `OpenTSDB` uses empty Pebble options. Storage writes an 8-byte float under a repeated node/metric/time key. No retention deletion path was found in the inspected backend. | Compression alone cannot bound long-term growth. Repeated keys and one entry per point contribute overhead. | P1/P2 |
| `Pipeline.Subscribe` queues 100 events per node subscription and drops incoming events when full. `latest` and registry node maps have no removal policy in the inspected paths. | Slow clients can retain old snapshots and see stale data; churn can grow maps. Shared pointers avoid full copies per subscriber, but queues retain referenced objects. | P1 |
| [CollectorTelemetryDataProvider.cs](MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry/Services/CollectorTelemetryDataProvider.cs): subscribes to every discovered node, posts each sample to the dispatcher, materializes process models, and rebuilds sparkline arrays. | Fleet growth multiplies streams, retained pending events, UI work, allocations, and string formatting. | P1 |
| [HostMetricsTabViewModel.cs](MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry/ViewModels/HostMetricsTabViewModel.cs): live updates copy timestamp/value arrays and regenerate labels. Pruning is time-based without a hard display-point bound in this path. History uses nested `Task.WhenAll` across graphs/nodes. | Long-running wide scopes grow far beyond the initial 1,200 historical points; simultaneous queries amplify server load. | P1 |

### Useful optimizations already present

Preserve the collector's atomic `PutRecordsBatch(..., pebble.Sync)` and ACK-after-persistence behavior, latest-timestamp protection, WAL segment metadata, offline payload filtering, process output cap, reused .NET channel, and polling-overlap guard. These are not new work to propose.

[MetricHistoryChartControl.cs](MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry/Controls/Charts/MetricHistoryChartControl.cs) already caches geometry and decimates points by pixel column. Rendering fewer points does not reduce the arrays retained upstream. [LiveLODBucketAccumulator.cs](MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry/Services/LiveLODBucketAccumulator.cs) already offers bucket aggregation, but its bucket size assumes one sample per second; reuse its intent with timestamp-based boundaries.

## 3. Compression policy by layer

| Layer | Proposed policy | Reason / implementation condition |
|---|---|---|
| Live lists, maps, per-core arrays, chart windows | Keep uncompressed and bounded | Direct access and mutation dominate; avoid serialization on every update. |
| Old immutable in-memory history | Optional zstd chunks with index and bounded decoded cache | Only useful when repeatedly querying the collector is more costly; otherwise evict and refetch. |
| Daemon WAL | Independent compressed records in larger physical segments | Recovery and acknowledgements require record boundaries; compression should not determine the durability unit. |
| Daemon → collector | Retain existing application-level compressed envelope initially | Both endpoints already understand it. Add byte thresholds and reuse immutable encoded records where practical. |
| Collector → UI historical responses | Benchmark negotiated gRPC zstd; compare identity and gzip | This path currently has no application zstd envelope. A Go codec and .NET provider are required. |
| Collector → UI live feed | Filter categories first; compress sufficiently large snapshots only | Tiny summaries may not repay codec cost. Process-heavy snapshots are a stronger candidate. |
| Pebble SST blocks | Separate Snappy-versus-zstd experiment | Use the storage engine's block compression; do not wrap database files or compress each 8-byte value. |
| Backups, exports, rotated substantial logs | Streaming zstd or indexed independent frames | Good cold-data candidates; use consistent DB snapshots/checkpoints for backups. |
| Small settings/theme JSON, binaries, compressed images | Leave as-is initially | Low expected return, added compatibility/startup complexity. |

Zstd frames expose information such as window requirements and optional content size; normal compression is not a general random-access container. Independent frames plus a seek index are the relevant model for cold history. See the [Zstandard format](https://github.com/facebook/zstd/blob/dev/doc/zstd_compression_format.md) and [seekable format specification](https://github.com/facebook/zstd/blob/dev/contrib/seekable_format/zstd_seekable_compression_format.md).

### Starting experiments, not production defaults

- Compare identity with `SpeedFastest` and `SpeedDefault` on actual protobuf payloads. In the pinned Go library these are presets; numeric zstd levels do not map one-to-one to distinct implementations.
- Try 1 KiB and 4 KiB minimum raw-payload thresholds, then keep compressed output only if it saves both a useful percentage (candidate: 10%) and absolute bytes. Count the envelope/framing in the comparison. Periodically reassess incompressible payload classes rather than spending CPU compressing every sample forever.
- Try 64, 256, and 1,024 KiB uncompressed block targets for cold data. Bound block size by both bytes and age; never delay a live sample merely to fill a large compression block.
- Start daemon codec concurrency at one; compare 256 KiB and 1 MiB windows. Reuse a bounded number of codecs and buffers, close owned codecs at shutdown, and avoid pools retaining arbitrarily large backlog buffers. Encoder window, concurrency, and low-memory controls are documented in the [pinned v1.20.0 encoder options](https://raw.githubusercontent.com/klauspost/compress/v1.20.0/zstd/encoder_options.go).
- Enforce compressed bytes, decoded bytes, window size, sample count, per-sample cardinality, and concurrent decode budgets independently. `WithDecoderMaxMemory`, `WithDecoderMaxWindow`, and optionally `WithDecodeAllCapLimit` support parts of this; their limits do not cover all resulting protobuf objects or total process RSS. The library's default non-streaming decoded-size allowance is 64 GiB, far above an appropriate telemetry-message budget. See [pinned decoder options](https://raw.githubusercontent.com/klauspost/compress/v1.20.0/zstd/decoder_options.go).

Large retained buffer capacity matters: current `EncodeAll(rawBytes, make([]byte, 0, len(rawBytes)))` reserves raw-size capacity even when compressed length is much smaller. For long-lived cached output, use appropriately owned storage sized for the retained compressed data; for transient output, compare this extra copying against bounded buffer reuse.

### Transport interoperability

The existing `TelemetryBatch.is_compressed/compressed_payload` is application payload compression, not a registered gRPC compressor. Applying gRPC compression to that same payload would add a second compression pass. Pick one compression owner per payload.

For query/live responses, add negotiated `zstd` support with identity fallback. gRPC compression is directional, and peers need not compress requests and responses identically. Compression history does not carry across gRPC messages, so a long-lived stream does not automatically compress like one large file. See [gRPC compression negotiation](https://grpc.io/docs/guides/compression/) and the [HTTP/2 message protocol](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md).

The .NET channel supports custom compression providers; zstd is not its default provider. [ZstdSharp](https://github.com/oleg-st/ZstdSharp) is a C# implementation candidate, not an already-installed dependency. Evaluate its streaming API, disposal, allocation behavior, and compatibility with the deployed runtime/RIDs before selecting it. See [.NET gRPC configuration](https://learn.microsoft.com/en-us/aspnet/core/grpc/configuration?view=aspnetcore-10.0).

For future persisted envelopes, specify codec, envelope/schema version, decoded-length limit, record identity, checksum, and optional dictionary ID. Retain old readers before enabling new writers. Dictionary training is a later experiment using representative training data and separate held-out data; require missing-dictionary handling, version distribution, and old-WAL recovery. Do not introduce it in the first rollout.

## 4. Collection and ingestion changes

### 4.1 Avoid work before it happens

Build an immutable effective collection plan when config/connectivity changes. Skip offline monitor-only probes before collection while preserving whatever counters are needed for stored metrics. On re-enabling a rate probe, reset or explicitly warm its baseline so the first rate is not computed across an unintended interval.

Use existing fast/normal/slow interval fields to schedule subsystems independently. Keep CPU/network/disk counters at the requested fast cadence; put inventory, power details, and less volatile metadata on appropriate slower cadences. Cache device discovery with refresh/invalidation for hotplug. Filter disabled devices before per-device sysfs work; reading shared `/proc` files may remain necessary for enabled aggregate metrics.

Concrete opportunities:

- Share a correctly parsed `/proc/stat` sample between CPU and process collectors, preserving consistent denominators and counter semantics.
- Avoid reading CPU frequency files every fast tick when frequency telemetry is not needed; handle unsupported paths with bounded negative caching.
- Split swap and zram probing so enabling one does not require collecting and then discarding the other.
- Cache NIC MTU/speed and power inventory with refresh policies; carrier state may need faster refresh.
- Run TWAMP on its own bounded schedule with cancellation and freshness timestamps. Reuse UDP sockets only with correct sequence validation and target-change handling. A missing reflector must not stall CPU/memory sampling.
- Use deadline-based scheduling with jitter across nodes; do not create overlapping collection work when a tick overruns. Record actual sample times and skipped/late ticks.

### 4.2 Process collection

Separate process CPU accounting from expensive detail materialization. Continue reading the counters required to find leaders, but select the bounded candidate set before creating full protobuf records and reading UID/status details where semantics permit. Compare a fixed-size heap (`O(P log K)`) with full sorting (`O(P log P)`), with P processes and K retained records.

Keep PID plus process start time as cache identity to handle PID reuse. Cap and prune metadata caches. Preserve the distinction between “top individual processes” and “top process names aggregated across workers”: selecting individual leaders first can change named-process totals. Make any reduced coverage explicit; a top-K algorithm reduces sorting/materialization, not the need to discover candidates.

### 4.3 Collector hot path

After byte bounds exist, pre-size records from an estimated metric count and reuse stable metric prefixes/IDs. Avoid repeated `fmt.Sprintf` and opt-in string parsing per point by compiling the config into lookup structures. Profile before introducing general object pooling, and preserve ownership of samples retained for live subscribers.

`PutRecordsBatch` already amortizes durability across a received batch. Measure Pebble's effective sync behavior before adding a collector writer queue. If a queue helps at scale, use byte/count/time bounds, preserve per-batch completion, and acknowledge only after the commit containing that batch is durable. Do not trade away durability by silently switching to `NoSync`.

## 5. WAL and transport redesign

### 5.1 Correctness and bounds first

- Make pull drain termination independent of compression. Return processing metadata or add validated outer metadata such as last timestamp, sample count, and `has_more`; avoid a second decode merely to inspect freshness. Empty spool responses should not trigger duplicate collection when a background sampler owns cadence.
- Replace sample-count-only replay limits with hard serialized/decoded byte budgets as well as count limits. `ReadBatchChunk` appends a whole segment before checking count, so multi-record segments require record-level cursors or another atomic boundary strategy.
- Validate impossible envelopes: compressed flag with missing payload, both raw/compressed forms when prohibited, mismatched identities, oversize records, and unreasonable cardinalities. Bound response sizes and concurrent decode work without simply raising all gRPC limits.
- Surface WAL write failures currently ignored by sampling loops. Audit partial writes, quota eviction failures, truncated tail recovery, directory-entry durability, and acknowledgement replay. Current quota accounting tracks logical file bytes, not filesystem blocks/inodes, and the active segment can exceed the configured quota.
- Compression config should have defined ownership. `main.go` creates the WAL using startup CLI options; remote `EnableZstdCompression` is not visibly applied to that existing WAL. Define startup-only versus runtime behavior and backward-compatible mixed-record recovery.

### 5.2 Separate records, segments, and acknowledgements

Use larger physical segments containing independently framed records. Rotate by bytes and age. Persist an acknowledged record cursor/sequence; delete only fully acknowledged segments. This requires an acknowledgement design change: current whole-file/range deletion cannot safely be retained while merely packing more records into each file.

Keep strict sync semantics as the first mode. An optional group-sync mode must state its maximum unsynced-loss window explicitly and must not report records as locally durable before sync. Merely putting several records in one file cuts file churn but does not itself reduce required strict syncs.

Keep expensive codec work outside the global WAL lock when possible. Pin segments or hold reader references so quota eviction/ACK cleanup cannot delete data during replay. Use a single ordered append owner and bounded queues, not unbounded parallel writers.

### 5.3 Encode once where possible

For a single record, reuse its immutable compressed payload for replay rather than decoding and recompressing it. For multi-record transport, an additive repeated encoded-record envelope can preserve those frames while keeping routing/ACK metadata outside them. Compare its extra per-record framing with recompressing larger groups; this is a measured tradeoff, not guaranteed savings.

Allow a bounded recent encoded-record cache after durable append to avoid immediate disk rereads. The WAL remains authoritative after restart. Eviction and disconnect must not lose unacknowledged data.

Current push/reverse push are effectively stop-and-wait. Approximate one-stream capacity is `batch bytes / (RTT + send + durable ingest time)`. Only if WAN replay is limited here, introduce a small in-flight byte window with explicit identities, retry ordering, duplicate handling, and per-record durable ACK tracking. A lost ACK must cause safe replay, not deletion. Keep live freshness and backlog drain budgets separate so a long outage does not monopolize the collector.

Replace the push path's 100 ms empty-spool polling with a wake-up notification plus cancellation. Apply capped jittered backoff consistently to pull/reverse reconnects; rate-limit routine poll logs. Preserve connection reuse where already present.

## 6. Storage and historical queries

### 6.1 Bound queries before compressing them

At 1 Hz, one metric over 30 days contains 2,592,000 points. The current Go `Point` fields alone occupy about 39.6 MiB at 16 bytes/point, before slice spare capacity, iterators, protobuf results, or concurrent requests. One hundred such concurrent raw scans imply roughly 3.9 GiB of point payload alone. These are arithmetic illustrations, not measured RSS.

Propagate cancellation/deadlines into the iterator loop. Add per-query time/scan/byte limits and a collector-wide admission budget. For display queries, scan into bounded time buckets retaining count/sum/min/max/first/last and select a bounded display result. This still scans raw storage, but avoids retaining it all. It changes exact LTTB behavior and must be documented and tested; do not describe arbitrary streaming bucketing as identical LTTB.

Persist rollups for frequently used long ranges to reduce scan I/O as well as memory. Retain raw data for a separately chosen period, then retain coarser buckets longer. Retention durations are product decisions; a trial such as 7 days raw / 90 days minute / 1 year hour is illustrative only. If all raw data must remain available, archive it in queryable chunks rather than silently discarding it.

Preserve data semantics:

- Current historical `MinValue` and `MaxValue` equal the selected point, not a bucket envelope. Populate real extrema if charts promise them.
- Aggregate hosts on aligned time buckets with explicit missing-data rules. Independently LTTB-sampled timestamps cannot reliably be summed or averaged by matching seconds.
- For counters, handle resets and compute rates/deltas before display downsampling. Use actual elapsed time and preserve exact integers where required; current float64 storage cannot preserve every uint64 above 2^53.
- Handle late/replayed points in rollups without double counting. The current raw key naturally overwrites the same node/metric/timestamp; rollup updates need equivalent idempotency and an explicit late-data policy.

### 6.2 Pebble compression: a separate decision

Pinned dependency: Pebble v1.1.5. Its options expose Snappy and zstd block compression; defaults use Snappy, no filter policy, and a cache of uncompressed blocks. Thus zstd SST compression can reduce disk/I/O without directly shrinking memtables or the uncompressed block cache. Benchmark per-level choices and explicit memory budgets; do not use current-master APIs for this pinned release. See [v1.1.5 options](https://raw.githubusercontent.com/cockroachdb/pebble/v1.1.5/options.go).

**Specific implementation concern:** the repository build sets `CGO_ENABLED=0`. The installed v1.1.5 source at `/home/danial/go/pkg/mod/github.com/cockroachdb/pebble@v1.1.5/sstable/compression_nocgo.go` constructs and closes a zstd encoder/decoder for each encode/decode call. This is a reason to measure codec initialization, allocation churn, and compaction CPU before enabling zstd throughout SST storage. Inspect an upgrade's implementation and compatibility if this dominates; do not introduce a maintained fork or CGO dependency by default. Corresponding upstream file: [compression_nocgo.go](https://github.com/cockroachdb/pebble/blob/v1.1.5/sstable/compression_nocgo.go).

Compare default Snappy, all-level zstd, and zstd only on colder levels. Observe table bytes, write amplification, compaction debt, sync latency, read latency, allocation rate, and CPU. Compression changes affect newly written/compacted tables; do not force a full live-database rewrite just to obtain immediate savings. Budget temporary disk space and rollback readability.

The architecture guide's prefix-Bloom claim is not supported by the inspected empty options and range iterator. Prefix filtering needs appropriate comparer/filter/seek configuration, and may not help a scan that actually needs all points in the range. Treat it as a workload-specific experiment.

### 6.3 Later: compact series chunks

If point/key overhead remains material, introduce versioned chunks keyed by series ID and time range, using delta or delta-of-delta timestamps, integer deltas, and float XOR encoding where lossless. Compare that encoding alone with encoding plus zstd. These ideas have a primary precedent in the [Gorilla time-series storage paper](https://www.vldb.org/pvldb/vol8/p1816-teller.pdf); its reported ratios are not MADTOM predictions.

This is a substantial schema project: define late writes, chunk sealing, series metadata/cardinality, mixed-version reads, migration, exact counter types, retention, and rollup interaction. Coordinate the actively developed [merge.go](MADTOM_GOLANG/pkg/collector/storage/merge.go), which currently copies existing key/value records and parses the current key shape. For bulk merge, byte-bound batches and benchmark sorted SST ingestion only after duplicate-key precedence and destination semantics are specified.

## 7. UI and live distribution

### 7.1 Bound history and eliminate repeated copying

- Use fixed-capacity rings for 60-point sparklines and bounded timestamp/value buffers for graphs. Establish a display budget based on viewport width with an upper cap, independently of time scope.
- Use timestamp-aligned live LOD buckets; merge historical and live data with stable identities and bounded points. Preserve min/max spikes. The existing accumulator's count-based 1 Hz assumption does not cover configured 100 ms sampling or delayed samples.
- Generate labels only for axis ticks/tooltips instead of every point on every update. Retain the chart's existing geometry caching and pixel decimation.
- Bound process overview history as well as metric histories. `HostProcessesTabViewModel` currently stores samples over the selected time scope and rebuilds overview arrays. Do not fabricate an advancing timestamp when a duplicate node snapshot arrives; deduplicate based on source time.
- Project only the required process data off the UI thread; apply bounded updates on the UI thread. Defer formatted memory strings and detailed rows until visible where possible.

A pair of timestamp/value arrays uses 16 bytes per retained point before labels and other overhead. At 2,400 points, 100 series use roughly 3.7 MiB of numeric payload; retaining 86,400 points per series uses roughly 132 MiB. Copying the latter arrays every update adds substantial allocation traffic. A ring/LOD cap addresses this directly; repeatedly zstd-compressing those arrays does not.

### 7.2 Bound pending live work

Use a latest-value slot per node and schedule at most one pending UI drain per bounded update cycle. Replace pending snapshots when newer ones arrive, measuring dropped/coalesced counts and sample age. On the collector, use a small latest-wins queue for display feeds rather than preserving 100 old snapshots while dropping fresh ones. This applies to live display only; durable ingest/backlog remains lossless within the declared quota/durability contract.

Honor `LiveSubscriptionRequest.subscribed_categories`, which the current server ignores. Send lightweight fleet summaries; request per-core/process detail when views need it. Share subscriptions among views with reference counting. Add explicit node/subscriber lifecycle cleanup without conflating removal from the live cache with deleting historical data.

Consider a batched multi-node live RPC when fleet size justifies it. The .NET client already reuses a channel, and modern `GrpcChannel` creates additional HTTP/2 connections when necessary; do not assume a universal 100-node hard limit. Measure actual stream/connection counts and call queuing. See [Microsoft's gRPC performance guidance](https://learn.microsoft.com/en-us/aspnet/core/grpc/performance?view=aspnetcore-10.0).

### 7.3 Bound history fan-out

Limit concurrent queries per collector and across the UI; start by testing 4 and 8 active requests rather than unbounded graph × host expansion. Deduplicate identical in-flight requests, cancel obsolete views, and use generation checks to prevent old completions overwriting new selections. Cache only byte-bounded immutable ranges; ranges ending at NOW need explicit freshness/invalidation. A future multi-series query can reduce RPC overhead and centralize aligned aggregation, but must retain response-byte and work limits.

## 8. Measurement and acceptance plan

Run these in a separate checkout/data directory once implementation begins. Keep profiler outputs and generated workloads outside this actively edited workspace. Do not benchmark against a live database that another process is modifying.

### Workload matrix

| Axis | Required cases |
|---|---|
| Hardware | amd64 collector, ARM64 endpoint, constrained ARMv7 endpoint; real low-end hardware for resource conclusions |
| Collection | 1 Hz and 10 Hz; small and high core counts; process mode off/live/stored; few and many NICs/disks |
| Fleet | 1 / 100 / 1,000 synthetic nodes, explicitly recording achievable load |
| Network | LAN and delayed/rate-limited WAN; reconnect storms; stalled peer and lost ACK |
| Backlog | Live only, 1 hour and 24 hours offline, quota pressure, compressed and legacy records |
| Queries | 5 minutes / 1 hour / 1 day / 30 days, cold/warm cache, concurrent multi-graph UI |
| UI | Fleet overview, detailed processes, long live scope, rapid view changes, slow/minimized client |
| Data quality | Constant gauges, bursty counters, high-entropy values, sparse data, resets, duplicate/out-of-order replay |

### Measure the whole resource envelope

- **Go:** CPU profiles, allocation and retained-heap profiles, mutex/block contention, goroutines, runtime memory, RSS, and GC time. Profile both steady state and replay.
- **.NET/UI:** allocation rate, retained heap, GC/large-object collections, RSS, dispatcher queue age, p95/p99 input-to-render latency, per-view retained histories, and stream/task counts.
- **Disk:** apparent bytes versus allocated bytes, file/inode counts, bytes written, fsync count/latency, Pebble cache/memtable usage, compaction debt and write amplification.
- **Network:** raw protobuf bytes, compressed bytes, actual transport bytes, RPC counts, retransmission/retry volume, and sample age at the UI.
- **Codec:** cold-start and warmed throughput, compression/decompression CPU, retained workspace, peak memory, ratio by payload class, and fallback frequency.

Do not tune `GOGC`/`GOMEMLIMIT` as a substitute for bounded data structures. The Go memory limit is soft and does not equal a total-RSS cap; leave headroom for non-Go memory and system needs. See the [Go GC guide](https://go.dev/doc/gc-guide). Likewise, change .NET GC mode only after measuring the desktop workload.

### Suggested promotion gates

These are proposed acceptance criteria to agree against actual deployment budgets, not claimed results:

1. Preserve stored values, timestamp ordering, configured opt-in behavior, crash/replay semantics, and cross-version readability. Explicit retention or coalescing policies must be tested separately from lossless changes.
2. Memory reaches a stable bound under a prolonged slow client, stalled network, long open chart, and large query; no growth proportional to elapsed session time beyond declared retention.
3. Compression has meaningful net byte savings (initial gate: at least 15% on its intended workload) without exceeding endpoint CPU/memory budgets or regressing p95 sample freshness/query latency by more than 10% relative to baseline. Report exceptions by hardware rather than averaging them away.
4. Backlog drains faster than new data arrives under the chosen load, while live freshness remains within its specified SLA.
5. Run crash/restart and lost-ACK tests for all three topologies, compressed/uncompressed mixtures, partial records, quota eviction, huge decoded payloads, and changed codecs/configuration.
6. Compare alternatives one at a time, then together. Report both median and tail latency, peak and steady RSS, and cold/warm behavior; retain raw benchmark results.

## 9. Implementation sequence

| Phase | Deliverable | Dependencies / exit criterion |
|---|---|---|
| P0: establish trustworthy limits | Baseline fixtures and metrics; compression-independent pull termination; byte/decode/query bounds; visible WAL errors | Demonstrate bounded failure behavior and unchanged successful ingestion |
| P1a: reduce production work | Effective collection plan, slower metadata cadence, timestamp-based scheduling, independent TWAMP | Equivalent enabled data semantics with reduced syscalls/CPU |
| P1b: bound memory and UI work | Ring/LOD histories, coalesced live updates, category filtering, query admission/cancellation | Stable memory and responsive UI under long sessions/slow consumers |
| P1c: improve existing compression | Size/savings policy, bounded codec reuse, recent encoded-record reuse | End-to-end byte/CPU/RSS benefit on supported architectures |
| P2a: restructure WAL | Multi-record physical segments, durable record cursors, safe replay readers | Recovery/ACK tests pass; materially fewer files and less replay contention |
| P2b: storage and query efficiency | Pebble codec experiment, persistent rollups, selected retention, optional negotiated UI zstd | Lower disk/query cost with defined semantics and migration/rollback |
| P3: conditional structural work | Compact series chunks, dictionary experiments, cold cache, in-flight replay window, multi-node RPC | Proceed only where profiles still identify these bottlenecks |

Features that change durability, retention, data resolution, or deployment dependencies need explicit product decisions at implementation time. Straightforward bounds, allocation reductions, and unnecessary-probe avoidance can be developed independently.

## 10. Documentation follow-up for the implementation phase

Leave existing documentation untouched during this research task. Once changes are implemented, synchronize CLI/config/UI/architecture documentation with verified behavior. In particular, recheck existing claims about fixed compression savings, universally compressed streams, prefix Bloom filters, segment sizing, and zero data loss: quotas and durability boundaries qualify those claims. Document the final per-layer policy and actual measured workload results instead of promising a blanket percentage.

**Final direction:** pursue broad resource efficiency with selective compression. Zstd is a strong candidate for larger serialized batches, cold history, and archives. Hot in-memory collections benefit first from bounded representations, less duplication, and less work; the storage engine and transport each need their own measured compression policy.


## Explicit WAL migration suite — 2026-09-18

- Added opt-in daemon `--migrate`, ordered version steps and persisted WAL version tracking. Completed steps are skipped; unsupported future versions fail safely. Normal startup does not migrate.
- Version 0 → 1 repacks raw/zstd legacy pending records into replay-sized records, preserving sample order and ACK prefixes with fresh sequence IDs. Atomic cutover intent and durable state allow explicit restart/resume after interruption.
- Added exclusive daemon spool ownership and startup blocking while a migration is incomplete. Migration staging bypasses quota eviction and requires temporary disk headroom.
- Limitations: oversized individual samples cannot be split by this migration; corrupt/incomplete or over-safety-bound records fail before cutover. No automatic downgrade, Collector DB migration or UI-cache migration. The new-write replay limit remains unchanged.
- Validation: migration tests cover raw/zstd oversized multi-sample records, ordering, partial ACKs, repeat runs, cutover interruption phases, oversized single-sample preservation, abandoned staging, future-version rejection, explicit-only behavior and exclusive ownership. Full Go race suite and documentation link validation run for this stage.


## Client compression diagnostics baseline — 2026-09-19

- NFLOG is explicitly Unimplemented; its implementation remains deferred.
- Replaced ZSTD Cache placeholder with current client compressed-memory status and a dense hover table for cache estimates and per-Collector/channel received protobuf counters. Reconnects retain counters; removed Collectors disappear. Refresh work runs only while hovered.
- Current client caches and channels do not use zstd: compressed memory is 0 B, ratios are unavailable. Remote daemon/WAL metrics, codec timings/failures and workspace usage require a future diagnostics protocol; do not infer them from client payloads.
- Collector ingestion limits are excluded from this UI stage. They remain a proposed resource safeguard for decoded allocations and concurrent ingestion, independent of this diagnostics work.
- Validation: added tests for concurrent accounting, uncompressed-cache reporting, channel reset retention, endpoint separation/removal and unknown remote metrics. Build, full .NET tests and documentation link checks run for this stage.

### Floating diagnostics panel follow-up — 2026-09-19

Replaced the constrained tooltip with an owned diagnostics window: hover preview, click/tap activation, pinning, draggable title bar, close/Escape, and a vertically scrollable body. Content stretches to the window width without a fixed inner width. Initial placement respects the screen work area; refresh and dismissal timers stop when closed. Added interaction-state tests alongside narrow-column layout tests. Native window movement and touch interaction still require manual desktop validation.

### Transport compression reporting — 2026-09-19

Added a separate transport section below client memory and additive Collector discovery fields exposing per-node/per-mode received batch counts, zstd payload bytes and decoded-zstd bytes. Ratios exclude raw batches, framing and TLS; retries count as received traffic. Counters reset on Collector restart. Existing daemons work unchanged; old Collectors remain Not reported. Compact 28px pin/close buttons replace the oversized title-bar controls, with a vector pin icon. Tests cover compressed/raw/malformed batches, retry counts, snapshot isolation, ratios and older-Collector fallback. No compression expansion or data migration is included.


### Client zstd expansion and raw traffic accounting — 2026-09-19

- Added lossless, selectively compressed 256-point live-history blocks and compressed stored-query results. Hot tails remain raw; retained-byte budgets include encoded/raw buffers and estimated metadata. Partial expiry rechecks budgets, and shrinking below one block preserves recent samples where the budget permits.
- Added negotiated zstd response envelopes for inventory, history, live telemetry and configuration. Updated clients opt in; old clients retain raw responses, and new clients accept old servers. Shared codec ownership, a 1 KiB minimum, 10% minimum savings, exact length checks and a 16 MiB decoded-envelope ceiling apply. No daemon changes or migration are required.
- Added separate uncompressed-byte and total-payload counters for client-facing channels and daemon ingestion. Ratios continue to exclude raw traffic. Memory diagnostics show actual retained zstd payload bytes, decoded equivalents and total retained estimates.
- Tests cover lossless timestamp/floating-point round trips, incompressible fallback, cache reuse/clear/budget/expiry behavior, malformed/nested envelopes, old-peer negotiation, unary/streaming gRPC compression, raw counters, and Go-to-.NET zstd interoperability. Production CPU/RSS and workload-specific compression savings remain to be profiled; codec workspaces and temporary decode buffers are outside cache estimates.

Validation completed: .NET build succeeded; all 200 .NET tests passed. Full Go race suite passed, followed by the added unary/streaming RPC negotiation tests. Documentation link validation and `git diff --check` passed. No deployment or migration was run.


### Stored-query admission and eviction correction — 2026-09-19

Raised per-result admission from 10,000 points to the requested **64K (65,536)**. Removed the independent 128-range eviction cap; configured retained-byte budget and absolute age remain authoritative. Results exceeding either the per-result point ceiling or their entire byte budget are displayed without being cached and cannot evict useful entries. Compression now runs outside the cache lock; generation/cancellation checks still prevent stale fills after clears/settings changes. Concurrent fills for identical intervals retain one result without allowing a later coarser result to replace finer data.

Regression tests cover 10,001–65,536-point cache hits, the 65,537 boundary, more than 128 retained ranges, oversized-result preservation, concurrent duplicate/coarser fills and absolute-age expiry. The 30-second default age remains configurable and is explained in the UI tooltip and documentation.

Validation: build succeeded; all 217 .NET tests passed, including the 64K boundary and admission/eviction regressions. Documentation links and whitespace checks passed. No Collector or daemon changes are required for this correction.

### Rolling stored-query cache reuse — 2026-09-19

Relative scopes move their end time on every visit. Previously, a cached range could only serve that new end when live samples covered it; nodes with sparse, delayed or absent live samples repeatedly fetched entire ranges and accumulated overlapping entries. Queries now reuse a sufficiently detailed cached prefix and fetch only the uncovered tail. A successful tail fetch replaces the rolling entry at the same requested resolution, retaining the original expiry so late-arriving history can still refresh. Empty tails are cached; failures preserve available history without claiming coverage. Clear/settings generation checks and the 64K admission ceiling still apply.

Validation: build succeeded and all 221 .NET tests passed. Four new regressions cover repeated rolling visits without live coverage, empty tails and original expiry, failed tails and clearing during a fetch, and rejecting insufficient resolution when zooming. Documentation links and whitespace checks passed. This correction requires only a client update.

### History-load timing diagnostics — 2026-09-20

Added opt-in `MADTOM_HISTORY_TIMING=1` JSON timing logs on stderr for both desktop clients. Refresh/span/parent IDs correlate configuration lock/cache/RPC work, live/stored/partial cache outcomes, decoding/merging/compression, request coordination and RPC stages, and graph series transformation/sampling. Counts, time ranges and collector/node/metric identifiers allow fixed-scope comparisons between slow and healthy nodes. No sample values are logged. Normal runs leave diagnostics disabled; a bounded background output queue avoids blocking graphs and reports dropped-entry counts. Timings cover view-model publication, not final painting. Capture instructions and field interpretation are documented in the UI guide and linked from the CLI guide.

Validation: build succeeded; all 224 .NET tests passed. New tests verify correlation across concurrent asynchronous spans, cache miss/hit/partial-hit reporting, cancellation reporting, and isolation from diagnostic sink failures. Documentation links and whitespace checks passed. No Collector or daemon update is required.

### History/live timestamp alignment diagnostics — 2026-09-20

Extended the existing opt-in timing logs with requested/cache/returned sample timestamps, full-window and tail coverage explanations, and the first live graph update after each completed history load. Coverage diagnostics share the production decision path and distinguish empty history, missing start, sample gaps, conservative sealed-block gap metadata, and stale ends. Client metrics receipt time is captured before UI dispatch; first-live logs compare receipt, UI observation, and graph window bounds and link back to the history refresh. Graph alignment and cache acceptance behavior remain unchanged.

Validation: build succeeded; all 227 .NET tests passed. New regressions cover coverage reasons including conservative block checks, stale-tail timestamp reporting, and one-shot before/after graph-window logging. Documentation links and whitespace checks passed. Reproduce 1m → 12h → 1m on both nodes with the existing timing environment variable; only the client needs updating.

### Per-node process snapshot size and storage clarification — 2026-09-20

Added a compact Live snapshot processes control (1–1,000 PIDs; default 1,000) alongside the separately labelled Stored process names control (existing 1–10; default 5). The additive NodeConfig process_snapshot_limit field travels through configuration RPCs and persistence to the daemon's top-process heap; omitted/zero values preserve the legacy 1,000 limit. Smaller snapshots reduce retained details, transmitted records, and client process history while retaining all-process counter scans needed to identify CPU leaders. Stored name groups are selected from the received snapshot, so lowering the PID count also narrows their candidate set.

The storage audit confirmed Collector TSDB writes only the selected top name groups plus other, while client graph queries merge all available live process-name CPU history. The ranking changes per sample, allowing low-CPU names to qualify when other processes are idle and allowing more than N distinct names over time. Updated UI help and documentation to distinguish this from persisting every live process. Existing history is not deleted or migrated.

Validation: .NET build and all 228 tests passed; full Go race-test suite passed. Tests cover snapshot selection/default/bounds and detail-read counts, configuration RPC validation/round-trip, persistence across restart, UI change tracking/revert, and exclusion of low-ranked processes from TSDB. Documentation links and whitespace checks passed. Upgrade client, Collector, and daemons to use the new limit; no data migration is required.
