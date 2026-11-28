# Abuse bans: one ban that follows a person into every chat

Requested 2026-11-28. Nox can already `ignore_user` someone inside one chat. Abusers also move between
groups and into Nox's DMs. A ban is the step up: it is global (keyed by Telegram user id), escalates, and
answers the banned person automatically wherever they try to use Nox. ADMIN_USER_IDS can never be banned.

## Decisions (from the user)
- A banned person **never wakes Nox** (no explicit turn, no ambient turn, no preprocessor invoke). Their
  group messages are still saved, so the rest of the conversation keeps its context.
- **No hard enforcement on Nox's side**: Nox may still `<reply>` / `<react>` to them when it happens to be
  awake for someone else. It is *advised* not to: their transcript lines carry a ` [banned]` tag, and a
  `[TIP]` (ban:<user_id>) explains the ban once per session.
- Nox picks the mode per ban: `notice` (automated "you are banned for N days" text), `react` (an emoji
  on their message, e.g. 🖕, instead of a message) or `silent`.
- `ignore_user` in a DM becomes a ban. In a group, `ignore_user(scope="global")` bans.

## Durations
First ban `first_days` (7). Every later strike multiplies by `multiplier` (2): 7, 14, 28, 56 ... up to
`max_days`. Strikes reset after `strike_reset_days` without a ban. Re-banning someone already banned only
updates mode/emoji/reason (no instant double strike).

## Where it hooks in (NoxChat)
| Path | Behaviour for a banned sender |
|---|---|
| `TelegramRuntime.trigger()` (messages, audio completion, recovery) | returns (False, False); if they addressed Nox, queues the automated response |
| `_preprocess_message` | skipped (no invoke notes) |
| command router (outer middleware) | command not run; automated response |
| guest / inline query | answered with the notice (or the emoji); no LLM, no quota |
| prompt builder | ` [banned]` sender tag + `ban:<id>` tip |

Automated responses are rate limited per (chat, user): `notice_cooldown_hours`.

## Guards against manipulation
- Evidence required: `evidence_message_ids` must be messages in this chat sent by that person.
- `max_bans_per_chat_per_day` caps how many bans one chat can produce.
- ADMIN_USER_IDS exempt; persona text: ban only for abuse aimed at Nox itself, never on request.
- Admin commands `/bans` and `/unban <user_id>`; Nox can lift a ban (`ignore_user(action="unignore", scope="global")`).

## Storage
`user_bans` (one row per user: strikes, banned_until, mode, emoji, reason, evidence, source chat, lifted)
and `user_ban_log` (ban / update / lift history; used by the daily cap and Studio).

## Studio monitoring
Python RPCs `list_bans` / `ban_action(lift)` → Go probe `ListBans` / `BanAction` → NoxAI "Banned users"
view (user, strikes, remaining time, mode, reason, evidence, source chat, Lift button).

## Config (`telegram.abuse_bans`)
`enabled`, `first_days`, `multiplier`, `max_days`, `strike_reset_days`, `notice_cooldown_hours`,
`max_bans_per_chat_per_day`, `suggest_after_chats`, `notice_text`.
