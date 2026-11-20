# Preprocessors and crons — contracts

The plan is in [README.md](README.md). Every limit listed here is a `config.yaml` key; nothing is hardcoded.

## 1. config.yaml

```yaml
preprocess:
  enabled: true
  max_per_chat: 10               # preprocessors per chat, static ones (made by cron create) included
  max_code_bytes: 16384
  max_state_bytes: 16384         # JSON state kept between runs; larger new state -> run error, old state kept
  max_actions_per_run: 5         # extra actions are ignored and logged
  max_say_chars: 4000
  max_note_chars: 1000
  max_payload_bytes: 4096        # cron payload JSON
  exec_timeout_ms: 300           # sent as timeout_ms; the sandbox also clamps to sandbox.pre_vm.exec_timeout_ms
  max_runs_per_minute: 30        # per chat, code (VM) runs; over -> skipped (pass), logged
  max_says_per_hour: 30          # per chat; over -> blocked, logged
  max_consecutive_says: 5        # preprocessor Nox messages without an LLM call in between
  on_consecutive_limit: invoke   # invoke | drop
  error_disable_after: 5         # consecutive errors -> disabled + chat notice
  immune_owner_and_admins: true  # ADMIN_USER_IDS and Telegram chat admins/creator: drop -> pass
  admin_cache_seconds: 600
  notify_chat_on_change: true    # short chat notice on create/update/delete/auto-disable
  log_keep_per_chat: 1000        # preprocessor_log rows kept per chat (oldest pruned)
cron:
  enabled: true
  max_per_chat: 20               # active (enabled, not finished) crons
  min_interval_seconds: 300      # every recurring cron
  nested_min_delay_seconds: 300  # crons created under a cron origin (see §4)
  max_nested_depth: 3
  max_fires_per_chat_per_day: 200
  misfire_grace_seconds: 600     # a fire missed by more than this is skipped; at most one catch-up fire
  max_lifetime_days: 366         # expires_at upper bound
  default_timezone: UTC          # IANA name; used for `cron` and `at` without an offset
  max_wake_seconds: 60           # loop re-checks at least this often
```

Pydantic: `PreprocessConfig` and `CronConfig` in `src/config.py`, with fields `preprocess` and `cron` on the root config, using the `Field(ge=…)` bounds style of `SandboxConfig`. `on_consecutive_limit` is `Literal["invoke","drop"]`. Document both sections in `docs/configuration.md`.

## 2. Tables (`src/db/schema.py`, `CREATE TABLE IF NOT EXISTS`)

```sql
CREATE TABLE IF NOT EXISTS preprocessors (
  id INTEGER PRIMARY KEY AUTOINCREMENT, chat_id INTEGER NOT NULL, name TEXT NOT NULL,
  kind TEXT NOT NULL,                 -- 'code' | 'static'
  code TEXT,                          -- kind=code
  static_actions TEXT,                -- kind=static: JSON list of actions (say/invoke only)
  triggers TEXT NOT NULL,             -- JSON list: 'message' and/or 'cron'
  filter TEXT,                        -- JSON object (§3.2) or NULL
  priority INTEGER NOT NULL DEFAULT 100,
  enabled INTEGER NOT NULL DEFAULT 1,
  state TEXT NOT NULL DEFAULT '{}',
  error_count INTEGER NOT NULL DEFAULT 0, last_error TEXT, last_run_at TEXT, runs INTEGER NOT NULL DEFAULT 0,
  created_by_user_id INTEGER, description TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL, expires_at TEXT,
  UNIQUE(chat_id, name)
);
CREATE TABLE IF NOT EXISTS preprocessor_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT, chat_id INTEGER NOT NULL, preprocessor_id INTEGER,
  preprocessor_name TEXT, at TEXT NOT NULL, event_kind TEXT NOT NULL,   -- 'message' | 'cron' | 'test'
  message_id INTEGER, cron_id INTEGER,
  outcome TEXT NOT NULL,   -- pass | drop | stop | say | invoke | cron | error | skipped | blocked | immune
  detail TEXT,             -- short: error text, blocked reason, first 300 chars of a say
  duration_ms INTEGER
);
CREATE INDEX IF NOT EXISTS idx_pplog_chat ON preprocessor_log(chat_id, id);
CREATE TABLE IF NOT EXISTS crons (
  id INTEGER PRIMARY KEY AUTOINCREMENT, chat_id INTEGER NOT NULL, name TEXT NOT NULL,
  preprocessor_id INTEGER NOT NULL,
  spec_kind TEXT NOT NULL,            -- 'at' | 'every' | 'cron'
  spec TEXT NOT NULL,                 -- ISO datetime | seconds | 5-field cron expression
  timezone TEXT NOT NULL, payload TEXT NOT NULL DEFAULT '{}',
  next_fire_at TEXT, last_fire_at TEXT, fires INTEGER NOT NULL DEFAULT 0, max_fires INTEGER,
  enabled INTEGER NOT NULL DEFAULT 1,
  depth INTEGER NOT NULL DEFAULT 0, origin_cron_id INTEGER,
  created_by_user_id INTEGER, created_at TEXT NOT NULL, expires_at TEXT,
  UNIQUE(chat_id, name)
);
CREATE INDEX IF NOT EXISTS idx_crons_due ON crons(enabled, next_fire_at);
CREATE TABLE IF NOT EXISTS automation_state (
  chat_id INTEGER PRIMARY KEY, say_streak INTEGER NOT NULL DEFAULT 0, updated_at TEXT
);
```

All times are UTC ISO-8601 with a `Z` suffix. A cron with no `next_fire_at` has finished: it is either a one-shot that has fired or one that reached `max_fires`/`expires_at`. Finished crons stay listed as `enabled=0`.

## 3. Preprocessor contract

### 3.1 Code
Code is Python run in a Pre VM. It must define `handle(event, state)`, which returns either `{"actions":[...], "state":{...}}` or `(result, new_state)`. A missing `state` key keeps the old state.
- The sandbox client already has `pre_run(chat_id, code, event, state, timeout_ms)`, which returns `{"ok","actions","state","error","timings_ms"}`. The guest returns `ok:false` with `error` on timeout or exception.
- If the chat lacks a sandbox grant (`sandbox_allowed(chat_id, False)` is false), code preprocessors are `skipped`. Creating one is refused with "this chat has no sandbox; ask an admin for /sandbox on".

### 3.2 Host-side filter (all keys optional; all present keys must match)
```json
{"text_regex": "(?i)hello", "user_ids": [1, 2], "exclude_user_ids": [3], "triggered_only": false, "audio": null}
```
- `text_regex` uses Python `re`, compiled when the preprocessor is saved. The pattern may be at most 300 characters, and only the first 4096 characters of the text are searched.
- `triggered_only` means the event runs only when Nox would answer it anyway (`explicit`).
- `audio` is `true` for voice/STT only, `false` for text only, and `null` for both.
- The filter applies to message events only.

### 3.3 Events
```json
{"kind":"message","chat_id":-100,"message_id":55,"date":"2026-11-20T06:00:00Z",
 "text":"...", "audio":false, "reply_to_message_id":null,
 "user":{"id":1,"name":"Sara","username":"sara","is_admin":false},
 "explicit":true, "ambient":false}
{"kind":"cron","chat_id":-100,"date":"...","cron":{"id":3,"name":"daily","payload":{},"scheduled_at":"...","fired_at":"...","fires":4}}
```
- For audio, `text` is the transcription and `audio` is true. The event is built in `_audio_complete` after STT, using the requesting message.
- `user.is_admin` is the host's immunity check: an `ADMIN_USER_IDS` member or a Telegram administrator/creator.

### 3.4 Actions (validated on the host; unknown types become errors; at most `max_actions_per_run`)
| action | fields | effect |
|---|---|---|
| `pass` | – | nothing |
| `stop` | – | end the message chain; Nox behaves as it would have |
| `drop` | – | message events only: Nox does not answer this message (removed from the pending batch / not submitted) and the chain ends. **An immune sender turns it into `pass`** (logged `immune`). On a cron event it is ignored. |
| `say` | `text` (≤ `max_say_chars`), `reply_to` (optional message id, default: the event message for message events) | Sent as Nox **through `ActionDispatcher.dispatch_stream`** with the chat id and the reply target as `incoming_msg_id`. Subject to the streak, hourly and cron-origin rules (§4). Logged with the first 300 characters. |
| `invoke` | `note` (≤ `max_note_chars`) | **Message event:** Nox answers that message even when not triggered (submitted explicit), with `context = "[Preprocessor <name>]: <note>"`. **Cron event:** wakes Nox with `notify_task_result(chat_id, "cron-<id>-<fires>", "[Cron <name> → preprocessor <name>]: <note>", event_key=…)`, carrying the cron origin. |
| `cron` | `name`, exactly one of `in_seconds`/`at`/`every_seconds`/`cron`, plus optional `payload`, `preprocessor` (name; default this preprocessor), `max_fires`, `timezone` | Creates a cron through `cron.service.create(..., origin=current origin)`. Rejections are logged as `error`. |

`static` preprocessors hold only `say` and `invoke` actions and run on the host, with no VM and no sandbox grant needed.

### 3.5 Chain rules
- Message events: run the enabled preprocessors with `"message"` in `triggers` and an unexpired `expires_at`, sorted by `(priority, id)`, after the filter matches.
  - The chain stops at the first `drop` (not overridden by immunity) or `stop`.
  - `say` and `invoke` from several preprocessors are all applied, in order.
- Cron events run only the cron's preprocessor; it must list `"cron"` in its triggers. Static preprocessors made by the cron tool have `["cron"]`.
- Errors and timeouts count as `pass`.
  - `error_count++` and `last_error` are updated. A success resets `error_count`.
  - At `error_disable_after` errors the preprocessor is set `enabled=0` and the chat gets a notice.
- State: on success with an `ok` result, save the new state if its size is ≤ `max_state_bytes`; otherwise mark the run as an error and keep the old state.
- Order inside the runtime: the chain runs **before** `coordinator.submit` in `accept()` and `_audio_complete()`, while the ingress lock is held, so the drop decision is known first.
  - The `say` deliveries and cron-event invokes run afterwards as background tasks, serialised per chat, so the lock is never held during Telegram sends.
  - Message-event invokes change the submit itself, so they are applied inline.
- Never run for guest invocations, commands, bot senders, or chats where `preprocess.enabled` is false.

## 4. Limits enforced in `src/preprocess/` and `src/cron/`
- **Consecutive says.** `automation_state.say_streak` is incremented after each delivered `say`.
  - When a `say` would make it exceed `max_consecutive_says`:
    - `invoke` mode turns it into an `invoke` with note `"[blocked: >N preprocessor messages without you] <name> wanted to send: <text[:max_note_chars]>"`;
    - `drop` mode logs it as `blocked`.
  - **Reset to 0** in `ActionDispatcher.dispatch_agent_turn` right after the turn's first successful LLM call, through `preprocess.limits.note_llm_call(chat_id)`. Keep it cheap: one UPDATE only when the streak is non-zero, using an in-memory mirror.
- **Hourly says.** Count `say` rows in `preprocessor_log` over the last hour. If `max_says_per_hour` is reached, the say is `blocked`.
- **Runs per minute.** Count code runs per chat over a 60 s sliding window in memory. If `max_runs_per_minute` is reached, the run is `skipped`.
- **Cron origin.** `src/cron/origin.py` defines the context variable `current_cron_origin: ContextVar[Optional[CronOrigin]]`, where `CronOrigin(cron_id, fired_at: datetime, depth: int)`.
  - It is set while a cron event runs, including its say and invoke processing.
  - `PendingResponse` gets a new field `cron_origin: Optional[Any] = None`. `notify_task_result(..., cron_origin=…)` stores it.
  - `TelegramRuntime.respond` sets the context variable around the agent turn when any item in the batch carries a cron origin (use the earliest `fired_at` and the deepest `depth`).
  - When an origin is present, `cron.service.create` enforces three things, and otherwise rejects with `CronError("nested cron: …")`:
    1. `origin.depth + 1 <= max_nested_depth`;
    2. first fire ≥ `origin.fired_at + nested_min_delay_seconds`;
    3. for recurring crons, the minimum interval between the next 5 fires is ≥ `nested_min_delay_seconds`.

    The new cron gets `depth = origin.depth + 1` and `origin_cron_id`.
- **Every cron:**
  - recurring crons need an interval of at least `min_interval_seconds`, checked over the next 5 fires for `cron` expressions;
  - no more than `max_per_chat` active crons;
  - `expires_at` no later than now + `max_lifetime_days`;
  - `at` must be in the future;
  - `max_fires_per_chat_per_day` is counted from `preprocessor_log` cron events since 00:00 UTC; over the limit, the fire is skipped and logged.
- Cron expressions use `croniter` (add it to `requirements.txt`). Timezones use `zoneinfo`. A naive `at` is read in the cron's timezone.

## 5. Cron loop (`src/cron/service.py`)
- `CronService` is started from `TelegramRuntime.start()` when `cron.enabled` is true, and stopped in `close()`.
- It sleeps until the earliest `next_fire_at`, waking at least every `max_wake_seconds` and immediately when crons change (an `asyncio.Event`).
- **Due cron.** If it is late by more than `misfire_grace_seconds`, skip it (log `skipped`, reason `misfire`) and advance. Otherwise:
  - set `last_fire_at` and `fires++`;
  - compute the next fire from *now*, so missed intervals are not replayed;
  - finish the cron when it was `at`, or when `max_fires` or `expires_at` is reached;
  - run `engine.run_cron(cron)` as a task.
- **Restart.** On startup, enabled crons with `next_fire_at` in the past follow the same misfire rule, so there is at most one catch-up fire each.
- **Cleanup.** Deleting a preprocessor deletes its crons. If the chat was left (`is_chat_left`), its crons are disabled.

## 6. Nox tools (compact schemas; full rules in `tool_guide` topics `preprocessors` and `cron`)
- `preprocessor(action, name=None, code=None, triggers=None, filter=None, priority=None, description=None, expires_in_hours=None, test_event=None)`
  - Actions: `create|update|test|list|show|enable|disable|delete|logs`.
  - `test` runs the code against `test_event` (default: a sample message event) with a copy of the state. Returned actions are reported, not applied, and state is not saved; the run is logged with event kind `test`.
  - `show` returns the code and state, truncated.
- `cron(action, name=None, preprocessor=None, say=None, invoke=None, in_seconds=None, at=None, every_seconds=None, cron=None, timezone=None, payload=None, max_fires=None, expires_in_hours=None)`
  - Actions: `create|list|show|cancel`.
  - `create` needs exactly one of `preprocessor`/`say`/`invoke`. `say`/`invoke` create a static preprocessor named `cron:<name>` that is deleted with the cron.
- Both tools take hidden `chat_id` and `requester_user_id` from the tool context (the same mechanism as `sandbox_run`). They are registered when `preprocess.enabled` / `cron.enabled` is true.
- Add the guide topics `preprocessors` and `cron` to `personas/_guides.yaml` / `src/persona/guides.py`. Keep each tool description to one line.
- The guide must state:
  - the action list and event shape;
  - fail-open behaviour;
  - immunity;
  - the 5-in-a-row rule;
  - the nested-cron rule;
  - that changes are announced;
  - that Nox decides whether a user's request is reasonable, refusing ones that would silence or spam the chat.

## 7. Admin commands (`src/telegram/commands.py`, `ADMIN_USER_IDS` only, silent otherwise)
- `/preprocessors` lists this chat's preprocessors as `id name kind triggers enabled errors runs`.
- `/preprocessors enable|disable|delete <name>`.
- `/crons` lists this chat's crons as `id name spec next fire enabled`.
- `/crons cancel <name>`.

## 8. Control RPCs (`src/control/rpc_handlers.py`; probe passes them through; JSON keys = proto field names)
| python method | proto rpc | request | response |
|---|---|---|---|
| `automation_status` | `GetAutomationStatus` | `chat_id?` | `{chats:[{chat_id,chat_title,preprocessors,enabled_preprocessors,crons,say_streak,runs_last_hour,says_last_hour,fires_today,next_fire_at}], config_json, error}` |
| `automation_list_preprocessors` | `ListPreprocessors` | `chat_id?` | `{preprocessors:[Preprocessor], error}` |
| `automation_get_preprocessor` | `GetPreprocessor` | `id` | `{preprocessor:Preprocessor, code, static_actions_json, state_json, error}` |
| `automation_preprocessor_action` | `PreprocessorAction` | `id, action` (`enable`/`disable`/`delete`/`reset_state`/`reset_errors`) | `{ok, error}` |
| `automation_preprocessor_logs` | `ListPreprocessorLogs` | `chat_id?, preprocessor_id?, limit (≤200, default 50), offset` | `{entries:[LogEntry], total, error}` |
| `automation_list_crons` | `ListCrons` | `chat_id?` | `{crons:[Cron], error}` |
| `automation_cron_action` | `CronAction` | `id, action` (`enable`/`disable`/`cancel`) | `{ok, error}` |

The object fields are exactly those of `NoxAutomationPreprocessor`, `NoxAutomationLogEntry` and `NoxAutomationCron` in `noxchat.proto`. Changes made through the Studio actions are announced in the chat in the same way, as "by MADTOM Studio".
