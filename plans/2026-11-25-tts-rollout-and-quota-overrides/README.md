# TTS rollout (deploy 1) + per-user /changequota overrides (deploy 2)

Requested 2026-11-25.

## Deploy 1 — pending TTS work (agy-authored, reviewed here)
Source plan: `~/.gemini/antigravity/brain/bde91f3f-8205-4b87-b301-012f919d5602/implementation_plan.md`
(walkthrough.md next to it). Scope: `nox-tts` host daemon (air-gapped, /run/nox-tts/api.sock), Go vsock
proxy in nox-sandbox (`/s/v_53` for chat/temp VMs only), `noxtts` client baked into the guest image,
Image/Document Studio voiceover tools, `kind="video"` documents, config + guides + control telemetry.

Steps: review security-critical code → local suites → commit → provision tp1 (apt, user, venv, models,
unit, host.yaml, nox-sandbox binary, guest image rebuild) → restart (no active jobs) → extensive tests on tp1
(full suites in a detached worktree + live TTS engine tests per engine + VM vsock allow/deny).

## Deploy 2 — /changequota per-user overrides
- Reply to a user: `/changequota -user <5h> <24h>`; anywhere: `/changequota -user-id <id> <5h> <24h>`
  → that user's limit everywhere (all chats).
- `... -current <5h> <24h>` → that user's limit only in this chat.
- `... -global <5h> <24h>` → that user's total across all chats (new per-user global counter).

### Design (deploy 2)
Today the quota is per chat only: `llm_call_log(chat_id, called_at)`, limits = `chats.quota_5h/24h` override
or `rate_limit.{dm,group}_limit_*`. Nothing counts per user.

Tiers (all 0 = unlimited, all checked; the first exhausted one refuses the call):
1. chat limit (unchanged): every call in the chat.
2. user-in-chat limit: calls triggered by user U in chat C. Limit = override (U, C) from `-current`, else
   U's all-chats override from plain `-user`, else `rate_limit.user_limit_5h/24h` (new, default 0 = off).
3. user global total: calls triggered by U across all chats. Limit = override from `-global`, else
   `rate_limit.user_global_limit_5h/24h` (new, default 0 = off).

Storage:
- `llm_call_log.user_id INTEGER` (new nullable column, migration; index `(user_id, called_at)`): the
  "global, per user" counter. NULL for calls with no human trigger (cron, task follow-ups).
- new table `user_quota_overrides(user_id, scope, chat_id, quota_5h, quota_24h, updated_at, updated_by,
  PRIMARY KEY(user_id, scope, chat_id))`, scope ∈ {`all_chats` (chat_id 0), `chat`, `global` (chat_id 0)}.

Who is charged: the batch's requester (latest human message in the active batch, the existing
`requester_user_id`). Cron / background follow-ups stay user-less (only the chat tier applies, as today).
Admins (ADMIN_USER_IDS) are exempt from the user tiers.

Command (admin only, silent for others; reply target or `-user-id <id>`):
- `/changequota -user <5h> <24h>` / `-user-id <id> <5h> <24h>` → scope all_chats
- `... -current <5h> <24h>` → scope chat (this chat)        - `... -global <5h> <24h>` → scope global
- `... reset` with the same flags removes that override; no numbers → show the user's limits + usage
- existing `/changequota <5h> <24h>` / `reset` (chat level) unchanged; `/quota` shows the caller's personal limits too.
Exceeded notice: personal wording ("your quota in this chat" / "your total quota"), one-time notice keyed per
(chat, user, window). Monitoring: Control Center RPC for user overrides + per-user usage; MADTOM Studio
NoxAI plugin view (agy Neon delegate).

Files: `progress.md` (checklist + log), `machines.md` (every change per machine/environment).
