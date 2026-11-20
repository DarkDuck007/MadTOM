# Preprocessors and crons — plan

Date: 2026-11-20. This is phases 3 and 4 of [the sandbox plan](../2026-11-20-nox-sandbox/README.md). The Pre VM path (`POST /v1/pre/run`) is already built and live on tp1. Exact interfaces are in [contracts.md](contracts.md); progress is in [progress.md](progress.md).

## What the owner asked for (2026-11-20)
- Crons, and preprocessors that can either invoke Nox or send a message as Nox without another LLM call.
- A preprocessor's Nox message goes through **exactly the LLM output pipeline**: `ActionDispatcher.dispatch_stream`, which runs the tag parser and the event processor. The only addition is the nested-cron rule below.
- **Nested-cron rule.** A cron created from a cron-originated run cannot fire sooner than 5 minutes after the cron that started the chain.
- **Consecutive-message rule.** No more than 5 preprocessor Nox messages in a row in a chat without a single LLM call in that chat.
- Every value is configurable in `config.yaml`.
- Standing rules still apply:
  - preprocessors are chat-bound and anyone can ask for one, with Nox deciding within guardrails;
  - the owner and chat admins are immune to drop;
  - MADTOM Studio gets monitoring;
  - I write all tests;
  - Studio UI goes to agy.

## Decision: crons only run preprocessors
The owner offered two options. I chose **"crons run preprocessors only"**, because it gives one execution path for every automated action:
- one place for limits, logging, the consecutive-message rule and the nested-cron rule;
- one Studio view.

For the common "remind me in 2 h" case, Nox does not write code. `cron(create, say=… | invoke=…)` creates a **static preprocessor**: a stored action list run on the host with no VM and no code. So simple reminders also work in chats without a sandbox grant. Code preprocessors need the chat's sandbox grant because they run in a Pre VM.

## Design

```
Telegram message ─► runtime.accept / _audio_complete (after STT)
                      └─► preprocess.engine.run_message_chain(event)   (priority order, fail-open)
cron loop (due) ───► preprocess.engine.run_cron(cron)                  (only the cron's preprocessor)
                         │ code: sandbox pre_run (Pre VM, single-use, no network, ~150 ms)
                         │ static: stored actions
                         ▼
                      actions: pass | drop | stop | say | invoke | cron
                         say    ─► dispatcher.dispatch_stream(text)   (exact LLM-output pipeline)
                         invoke ─► runtime: Nox turn (respects quotas), cron origin carried along
                         cron   ─► cron.service.create (nested-cron rule when a cron origin is set)
```

- **Message chain.** Every enabled preprocessor of the chat whose trigger is `message`, ordered by priority and then id:
  - host-side filters run first, so most messages never start a VM;
  - the chain stops at `drop` or `stop`;
  - `drop` suppresses Nox's reply to that message, except for immune senders, where it becomes `pass`;
  - `invoke` makes Nox answer the message even when it was not triggered, with the note as context.
- **Cron fire.** This runs only the cron's own preprocessor, with a `cron` event. Because there is no message, `drop` has no meaning there.
- **Consecutive-message rule.** Each chat keeps a `say_streak`:
  - every delivered `say` adds 1;
  - the first successful LLM call of any Nox turn in that chat resets it to 0;
  - a `say` that would exceed `max_consecutive_says` is handled by `on_consecutive_limit`: `invoke` (default) hands the text to Nox as a note, which costs an LLM call and resets the streak; `drop` blocks it and logs it.
- **Nested-cron rule.** A *cron origin* (`cron_id`, `fired_at`, `depth`) is attached to everything a cron fire causes:
  - the preprocessor's actions;
  - the `say` pipeline;
  - a Nox turn started by `invoke`, which carries it through `PendingResponse` and a context variable that the `cron` tool reads.

  Any cron created under an origin must:
  - have its first fire at or after `origin.fired_at + nested_min_delay_seconds`;
  - if recurring, have an interval of at least `nested_min_delay_seconds`;
  - have a depth no greater than `max_nested_depth`.

  Anything else is rejected with a clear error.
- **Fail-open.** An error, a timeout, a missing sandbox grant or a rate limit counts as `pass` and is logged. After `error_disable_after` consecutive errors, the preprocessor is auto-disabled and the chat gets a notice.
- **Visibility.** Creating, updating, deleting or auto-disabling a preprocessor or cron posts a short notice in the chat (`notify_chat_on_change`).
  - Admins (`ADMIN_USER_IDS`) can use `/preprocessors` and `/crons`.
  - Studio gets an **Automation** tab.

## Loopholes and fixes
1. **Runaway message loop.** A cron fires a preprocessor that says something, repeatedly. It is bounded by:
   - `max_consecutive_says` (5), after which the text goes to Nox through `invoke`;
   - `max_says_per_hour`;
   - `max_fires_per_chat_per_day`.
2. **Cron chain explosion.** A cron creates crons that create crons. It is bounded by:
   - `nested_min_delay_seconds` (300);
   - `max_nested_depth` (3);
   - `max_per_chat` (20);
   - `min_interval_seconds` for every recurring cron.
3. **LLM cost by `invoke` on every message.** `invoke` goes through the normal Nox turn, so chat quotas (`QuotaService`) apply. `max_runs_per_minute` also caps VM-backed runs.
4. **Hijacking the chat.** A preprocessor could drop every message so that Nox goes silent. Fixes:
   - owner and chat admins are immune to `drop`;
   - every change is announced in the chat;
   - admins can disable with `/preprocessors`;
   - the error auto-disable rule applies.
5. **Untrusted code.** Preprocessor code comes from Nox, possibly at a user's request. It runs only in a single-use Pre VM with no network and no disk, under a host time limit (300 ms), with state capped (`max_state_bytes`).
6. **Hostile Pre VM output.** Actions are validated strictly on the host:
   - known types only;
   - size caps on text, notes and payloads;
   - at most `max_actions_per_run` actions;
   - the chat is always the host's chat, so it is never taken from the actions.
7. **A `say` can carry any Nox tag**, including `<remember>`, `<pin>`, `<image>` and `<document>`. This is accepted because the owner chose "exact pipeline". It is limited by the streak and rate caps, and every say is logged with its text.
8. **Prompt injection through events.** Event text is data for the code. The `invoke` note reaches Nox labelled as preprocessor output, not as a user or system instruction.
9. **Missed fires during downtime.** A fire missed by more than `misfire_grace_seconds` is skipped, not replayed in a burst. At most one catch-up fire per cron.
10. **Guest chats and bot messages.** Preprocessors never run for guest invocations or for the bot's own messages. Commands (`/…`) are not passed to preprocessors.
11. **Deleting a preprocessor that crons point to.** Its crons are cancelled with it; the cancellation is announced in the chat.
12. **Immunity spoofing.** Admin status comes from Telegram (`get_chat_member`, cached for `admin_cache_seconds`), never from the event or the code.

## Work split
- **Me:**
  - this plan and the contracts;
  - the proto additions in `noxchat.proto`, plus regenerating the Go code;
  - the Go `PreRun` state fix (done);
  - all tests;
  - review;
  - the tp1 deploy and the live E2E test.
- **codex gpt-6.2-sol:** the NoxChat Python: config, schema, `src/preprocess/`, `src/cron/`, runtime and dispatcher hooks, tools, guides, admin commands and control RPCs.
- **codex gpt-6.1-sol:** the Go probe pass-throughs in `MADTOM_GOLANG/pkg/noxchat/server/server.go`.
- **agy gemini-4-argon-medium:** the Studio Automation tab in the NoxAI plugin.

## Verification
- **pytest (mine):**
  - fake sandbox client, fake clock, fake bot;
  - chain order, filters, drop, immunity, stop, invoke, fail-open and auto-disable;
  - consecutive-say limit in both modes, and the streak reset by an LLM call;
  - the nested-cron rule on first fire, interval and depth;
  - static crons, misfire skipping, cancelling on delete;
  - rate limits and cron recovery after a restart.
- **Go:** probe wire tests. **C#:** ViewModel tests.
- **Live test on tp1** in chat 1998313581:
  - a "say hi to every message containing X" preprocessor;
  - a `drop` aimed at an admin, which must not take effect;
  - a 2-minute reminder (static);
  - a cron that tries to create a 1-minute nested cron, which must be rejected;
  - a 6th consecutive say, which must invoke Nox instead.
