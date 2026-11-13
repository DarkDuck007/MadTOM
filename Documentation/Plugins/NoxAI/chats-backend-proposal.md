# Chats backend integration

This guide records the approved and implemented backend integration for the Studio Chats tab. Correlation metadata connects saved messages, captured model calls, and completed execution events without relying on chat names. Existing records without correlation metadata remain readable in separate unverified legacy groups.

## Compatibility with earlier versions

`ListChats` already returns stable chat IDs, types, and titles. `GetChatHistory` returns saved messages. Earlier Python handlers cap the chat list at 100, return recent nonarchived messages without cursors, and assign the request time to every returned message rather than using its stored creation time.

`NoxRawApiCallRecord` contains request/response bodies and a heuristic `chat_name`, but no authoritative chat or turn identifier. A title match cannot distinguish identically named groups, renamed chats, or shared auxiliary tasks. Studio keeps legacy trace groups explicitly unverified and separate from saved chats.

The orchestrator creates AGY metadata (`chat_id`, `session_epoch`, `turn_index`, `is_bootstrap`, `conversation_id`, and `history_mode`) in `src/persona/prompt_builder.py`; `src/telegram/dispatcher.py` adds `tool_step`. Capture now preserves this metadata alongside request/response bodies. Ordinary turns and auxiliary work use a task-local execution context as well.

## Additive protocol

Existing field numbers and RPCs are preserved. New clients detect pagination support explicitly and fall back when an older probe does not implement `QueryChatTrace`:

| Location | Addition | Purpose |
|---|---|---|
| New `NoxTraceContext` message | `optional int64 chat_id`, `session_id`, `run_id`, optional `turn_index`, optional `step_index`, `conversation_id`, `is_bootstrap`, and triggering message IDs | Authoritative correlation independent of display names; a present chat ID of `0` identifies the Control Center sandbox, while an absent ID means unknown. |
| `NoxRawApiCallRecord` | `NoxTraceContext trace_context = 16` | Carry context through bridge frames, live chunks, spool records, summary responses, and detail lookup. |
| `NoxListChatsRequest` / response | Bounded `limit`, opaque `cursor`, `next_cursor`, explicit pagination support/completeness | Discover all saved chats without a fixed 100-chat truncation. |
| `NoxChatHistoryRequest` / response | Bounded `limit`, opaque `cursor`, `include_archived`, `next_cursor`, explicit pagination support/completeness | Read all retained saved messages in stable pages without changing the current default recent-message behavior. |
| New `NoxChatTraceEvent` | Stable `event_id`, trace context, occurrence timestamp, event kind, content, attributes, optional related API call ID | Preserve actual RAG/tool/action outcomes outside API prompt snapshots. |
| New `QueryChatTrace` RPC | Chat ID with presence, optional session/run filter, bounded limit/cursor; ordered API-call summaries and trace events, next cursor, retention/completeness metadata | Retrieve a chat's trace without downloading and decoding every retained request body. Full API bodies continue to use `GetApiCallDetail`. |

Capture uses an execution `run_id` scoped to a bot invocation, including in ordinary non-AGY operation. Reuse AGY session and turn metadata when available, but do not treat a session epoch alone as globally unique across process restarts. Preserve the returned provider conversation ID as well as the locally established session/run identifiers. Propagate context explicitly or with a scoped context variable so concurrent chats and child tasks cannot overwrite one another's identity.

## Capture and persistence

1. Preserve the complete captured request/response JSON. Add correlation metadata alongside it rather than inserting synthetic messages or rewriting old bodies. Retain tool-call IDs so tool results can be linked to the correct invocation.
2. Capture the initial system prompt at bootstrap, and retain later system/context changes with their original steps. Prompt snapshots show the RAG context supplied to the model; they must not be presented as proof that an unrecorded retrieval action occurred.
3. Emit correlated events at existing execution boundaries for RAG results, tool invocation/result/error, and bot actions whose outcomes are not represented by raw API completions. Use the same bridge and store policy. Do not persist every streamed token as a separate historical event; retain completed outputs and existing raw bodies.
4. Add a bounded trace-event WAL envelope and a chat-to-retained-record lookup rebuilt during normal spool recovery. Keep it under the existing spool quota and eviction rules. `OnTurnEvent` currently only appends to the pending live state list; that is insufficient for historical inspection.
5. Index API summaries by authoritative context as records arrive/recover, and preserve context when building summary-only records. Page using stable record identities and snapshot bounds; reject repeated or invalid cursors and report when quota eviction invalidates a cursor. Query work must honor cancellation and scan/page budgets.
6. Return stored message creation timestamps, with explicit absence for invalid/unknown values. Use database message IDs as paging tie-breakers. Reading archived messages for inspection must not modify their archived state or the model's active prompt history.

## Implementation files

Paths in this table are relative to the repository root; focused tests accompany the changed modules.

| Files | Responsibility |
|---|---|
| `MADTOM_GOLANG/proto/madtom/v1/noxchat.proto` | Additive context, pagination, event, and query definitions. |
| `MADTOM_GOLANG/pkg/proto/v1/noxchat.pb.go`, `MADTOM_GOLANG/pkg/proto/v1/noxchat_grpc.pb.go` | Regenerate the Go contract. C# protobuf bindings are generated during the Studio build. |
| `NoxChat/src/control/server.py`, `NoxChat/src/control/trace.py` | Emit correlation fields and completed trace events through the existing bridge. |
| `NoxChat/src/llm/client.py` | Preserve execution context, returned conversation IDs, tool IDs, and failure information in captured calls. |
| `NoxChat/src/telegram/dispatcher.py`, `NoxChat/src/persona/prompt_builder.py`, `NoxChat/src/control/direct_chat.py` | Establish context for AGY, ordinary bot turns, and Control Center turns; emit execution events at their actual boundaries. |
| `NoxChat/src/control/rpc_handlers.py`, `NoxChat/src/db/chat_history.py` | Add cursor-based read queries and return real message times. |
| `MADTOM_GOLANG/pkg/noxchat/bridge/bridge.go` | Decode and route the additive trace event frame. Existing API frames already deserialize the protobuf record. |
| `MADTOM_GOLANG/pkg/noxchat/server/server.go` | Forward pagination, preserve live event correlation, persist trace events, and expose bounded chat trace queries. |
| `MADTOM_GOLANG/pkg/noxchat/spool/spool.go` | Retain/index contexts and events, preserve summary metadata, and page retained chat traces across recovery/eviction. |
| Studio Chats source adapter and tests | Connect the already implemented Studio models to the approved new contract and capability behavior. |

Scoped execution context is established at direct-message, Telegram dispatcher, runtime response, prompt-building, and coalesced media-work boundaries. Child tasks inherit a snapshot of that context; context changes replace dictionaries so concurrent chats do not share mutable identity.

## Compatibility and verification

- Old records remain readable through the existing raw-call inspector. Missing context stays unknown; no automatic title-based reassignment or historical backfill is claimed.
- Old clients ignore additive fields. Old probes provide the existing recent-message/legacy-trace view, with visible limits. Studio must detect new pagination/trace support instead of assuming an empty continuation means complete history on an old probe.
- Stable chat `0`, same-title chats, concurrent invocations, session resets, auxiliary calls, and tool-call/result linkage need round-trip tests through capture, summary, detail, WAL recovery, and eviction.
- Test more than 100 chats and multiple message pages, equal timestamps, archived history, invalid/repeated cursors, cancellation, and gaps after retention expiry. Reading history must leave existing bot behavior unchanged.
- Verify initial-prompt retention, later prompt revisions, completed raw outputs, RAG/tool errors and successful outcomes, and duplicate event/call replay. A missing historical record must be identified as unavailable rather than reconstructed from current settings.

Build the updated Studio and probe and run the updated Python sources to enable the complete contract. Old clients and probes continue to use their earlier endpoints. Capture and retention still follow the configured log mode: monitor-only events are available live, while stored history is limited by the WAL quota. Previously uncaptured or evicted prompts and events cannot be recovered by this update.
