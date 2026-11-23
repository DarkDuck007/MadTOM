# SlideForge probe and plugin: contracts

## 1. Bot ⇄ probe (control channel)
- This is the NoxChat frame protocol over TCP 127.0.0.1:50058, sent by the bot (Python).
- Frames are `[kind u8][len u32 BE][JSON]`:
  - 0x01 MODE_SYNC
  - 0x02 LOG
  - 0x03 API_CALL
  - 0x04 TELEMETRY
  - 0x05 JOB_EVENT
  - 0x10 RPC request
  - 0x11 RPC reply
- RPC frames reuse `pkg/noxchat/bridge.RpcMessage`: the request is `{"req_id": "...", "method": "...", "payload": {...}}`.
- The reply is the same envelope with `payload`, or with a non-empty `error`.
- LOG, API_CALL, TELEMETRY and JOB_EVENT use NoxChat's JSON shapes (`LogRecord`, `NoxRawApiCallRecord`, `NoxTelemetrySample`, `NoxJobProgressEvent`), so the probe reuses `bridge.Client` unchanged. Its dispatcher converts them into the `Sf*` messages below.
- The method list is in SlideForge's `contracts.md` §13. The probe's allowlist is exactly that list. Unknown methods are refused in the probe, before they reach the bot.

## 2. Studio ⇄ probe (gRPC, TLS 1.3, Ed25519-signed metadata)
- Metadata is `x-nox-auth-ts`, `x-nox-auth-nonce` and `x-nox-auth-sig`, as in NoxChat.
- Server-side changes:
  - replay cache;
  - authorized-keys file (`-authorized-keys PATH`, one `noxchat-ed25519-pub:` key per line, `#` comments allowed);
  - persistent certificate in `<state>/tls/{cert,key}.pem`.

`MADTOM_GOLANG/proto/madtom/v1/slideforge.proto`:

```proto
syntax = "proto3";
package madtom.v1;

import "madtom/v1/logs.proto";
import "madtom/v1/query.proto";

option go_package = "github.com/DarkDuck007/madtom/pkg/proto/v1;madtomv1";
option csharp_namespace = "MADTOM.Plugins.SlideForge.Proto.V1";

service SlideForgeControlService {
  rpc GetNodeStatus(SfNodeStatusRequest) returns (SfNodeStatusResponse);
  rpc UpdateControlConfig(SfUpdateControlConfigRequest) returns (SfNodeStatusResponse);

  // zstd-chunked live streams (same buffers/chunker as NoxChat)
  rpc SubscribeState(SfStateSubscriptionRequest) returns (stream SfStateChunk);
  rpc SubscribeLogsAndApi(SfLogApiSubscriptionRequest) returns (stream SfLogApiChunk);

  rpc QueryTelemetryRange(RangeQueryRequest) returns (RangeQueryResponse);
  rpc GetSpoolStats(SfSpoolStatsRequest) returns (SfSpoolStatsResponse);
  rpc QueryLogChunks(LogChunkQuery) returns (LogChunkResponse);

  // Allowlisted JSON RPC passthrough to the bot (jobs, users, guides, tips, config, sandbox_*)
  rpc Call(SfCallRequest) returns (SfCallResponse);
}

message SfNodeStatusRequest { string node_id = 1; }

message SfNodeStatusResponse {
  string node_id = 1;
  string hostname = 2;
  string slideforge_version = 3;   // git describe of the bot checkout
  string probe_version = 4;
  string python_version = 5;
  int64 uptime_seconds = 6;
  bool bot_running = 7;            // control channel connected
  bool store_raw_logs = 8;
  uint64 max_spool_bytes = 9;
  uint64 current_spool_bytes = 10;
  repeated TransportCompressionStats transport_stats = 11;
  repeated string allowed_methods = 12;   // what Call accepts
  string tls_fingerprint_sha256 = 13;     // informational; the client pins from the handshake
}

message SfUpdateControlConfigRequest {
  string node_id = 1;
  bool store_raw_logs = 2;
  uint64 max_spool_bytes = 3;
}

message SfStateSubscriptionRequest { string node_id = 1; }

message SfStateChunk {
  string node_id = 1;
  uint64 chunk_id = 2;
  int64 timestamp_unix_nano = 3;
  bytes zstd_payload = 4;          // SfStateChunkPayload
  uint32 decoded_size = 5;
  uint64 dropped_chunks = 6;
}

message SfStateChunkPayload {
  repeated SfTelemetrySample telemetry_samples = 1;
  repeated SfJobEvent job_events = 2;
}

message SfTelemetrySample {
  int64 timestamp_unix_nano = 1;
  map<string, double> metrics = 2;  // SlideForge contracts §13 TELEMETRY keys
}

message SfJobEvent {
  int64 timestamp_unix_nano = 1;
  string job_id = 2;
  int64 user_id = 3;
  string kind = 4;                  // status|note|question|answer|tool|web|result|error|refund
  string status = 5;                // job status after the event
  string json = 6;                  // event payload (job_events.payload)
}

message SfLogApiSubscriptionRequest {
  string node_id = 1;
  bool include_raw_api_calls = 2;
}

message SfLogApiChunk {
  string node_id = 1;
  uint64 chunk_id = 2;
  int64 timestamp_unix_nano = 3;
  bytes zstd_payload = 4;          // SfLogApiChunkPayload
  uint32 decoded_size = 5;
  uint64 dropped_chunks = 6;
  string chunk_kind = 7;           // "log" | "api"
}

message SfLogApiChunkPayload {
  repeated LogRecord log_records = 1;
  repeated SfApiCallRecord api_call_records = 2;
}

message SfApiCallRecord {
  int64 timestamp_unix_nano = 1;
  string job_id = 2;
  string kind = 3;                  // tool|model|web
  string name = 4;                  // e.g. doc_tools.render_pptx
  double seconds = 5;
  bool ok = 6;
  string json = 7;                  // args/result summary (bounded by the bot)
}

message SfSpoolStatsRequest { string node_id = 1; }
message SfSpoolStatsResponse {
  uint64 current_bytes = 1;
  uint64 max_bytes = 2;
  uint64 log_chunks = 3;
  uint64 api_chunks = 4;
  int64 oldest_unix_nano = 5;
}

message SfCallRequest {
  string node_id = 1;
  string method = 2;
  string payload_json = 3;          // <= 1 MiB
  uint32 timeout_ms = 4;            // 0 = 30000; capped at 120000
}

message SfCallResponse {
  bool ok = 1;
  string payload_json = 2;          // may be zstd-compressed by the transport, not here
  string error = 3;                 // "method_not_allowed" | "bot_unavailable" | "timeout" | bot error text
}
```

`TransportCompressionStats`, `RangeQuery*` and `LogChunk*` come from the shared `query.proto` and `logs.proto`, so there is no dependency on `noxchat.proto`.

## 3. Probe flags (`cmd/slideforge-probe`, as built)
```
-node-id tp2 -listen :50057 -bot-host 127.0.0.1 -bot-port 50058
-state-dir /var/lib/slideforge-probe      (TLS identity in tls/, WAL spool)
-auth-key <key> | -auth-key-file <.env or key file>   (env SLIDEFORGE_CONTROL_KEY; default lookup ./SlideForge/.env)
-authorized-keys <file>                   (one noxchat-ed25519-pub: or ssh-ed25519 key per line, # comments)
-insecure-dev                             (required to start with no key at all)
-mdns=true  -max-spool-bytes 1073741824  -store-raw-logs=false  -gen-key
```
- `noxchat-probe` gained the same `-authorized-keys`, `-insecure-dev` and `-state-dir` (default `/var/lib/noxchat-probe`) flags. If the state dir is unwritable, it falls back to an ephemeral certificate with a warning.
- Both probes log `TLS certificate SHA-256: <hex>` at startup. Compare it with the fingerprint that the Studio pins (TOFU).
- `Call` errors: `method_not_allowed`, `payload_too_large` (> 1 MiB), `bad_payload` (invalid JSON or UTF-8), `bot_unavailable`, `timeout`, or the bot's error text.
