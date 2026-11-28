# Guest mode: anti-bullying, stickers, media viewing, no duplicate context

## Context
Guest mode lets someone @-mention Nox in a chat Nox isn't in (`src/telegram/guest.py`). Today it is one LLM stream with no tools, answered with one text result (`answer_guest_query`). Three gaps:

1. **No anti-bullying.** The BULLIES rule tells Nox to `ignore_user`, but guest mode has no tools. Global bans are already enforced for guest callers; nothing else is.
2. **Media.** Viewing is limited to a replied-to photo, and replies can only be text.
   - Scope agreed with the user: Nox may **see** replied-to media and **reply with a sticker**.
   - Out of scope: image studio, doc studio, sandbox and file sending.
3. **Duplicate context.** `build_guest_messages` (`prompt_builder.py:1313`) dates from before guest messages were stored.
   - The replied-to message is saved to the guest session and listed in GUEST SESSION HISTORY, then repeated in a `[Replied-to message …]` tail block.
   - The current request is saved but shown only as a tail block, so it has no `#id`.
   - When someone replies to Nox's earlier guest answer, the text Telegram returns is the rendered version. It doesn't match the stored text in `_find_matching_recent_message_id`, so a second copy gets stored.

Answering rule (Bot API 10.0): one reply per guest call, as an InlineQueryResult. A sticker goes as `InlineQueryResultCachedSticker` with the library's `file_id`.

## 1. Context dedup (`guest.py`, `prompt_builder.py`)
- Pass the current request into the history:
  - append `user_rec` to `prior_messages`;
  - pass `user_msg_id` and `linked_reply_id` to `build_guest_messages`.
- When the history holds those records, the tail becomes one line: `[Current request: #N from <caller> -> #M]`, plus the quoted excerpt if present.
  - Drop the `[Replied-to message]` block and the repeated request text.
  - Keep `references_context`.
  - If recording is off or there is no history, keep the old tail.
- Reply matching:
  - compare against both the stored text and its rendered plain-text form (`render_telegram_html` output, tags stripped), so replies to Nox's own answers reuse the existing `#id`;
  - match a sticker reply by `media_file_id`.
- Save `media_kind`, `media_file_id` and the vision description (`media_description`) on the stored reply record. Later turns then show `[Seen: …]` from history instead of downloading and running vision again. Vision runs only for media not already described in the session.

## 2. Viewing media (`guest.py`)
- Replace the photo-only download with the processors the normal turn uses (`runtime.py:798`): `process_photo`, `process_sticker`, `process_animation`, `process_video` (video_note too).
- Run them on the replied-to message and on media in the invoking message itself.
- Respect `config.llm.vision`. A failure falls back to a `[Sticker]`-style placeholder.

## 3. Small guest tool loop (`guest.py`, new helper)
- Replace the single `stream_chat` with up to `guest_mode.max_tool_steps` (new, default 2) calls to `llm.stream_step(messages, tools)`.
  - Same pattern as `dispatch_agent_turn` (`dispatcher.py:1109`) but without its Telegram sends, which assume a real chat.
  - The whole call stays inside `llm_timeout_seconds`. Quota is still charged once per guest call to the caller's DM (unchanged).
- **Allowlist:** only `ignore_user` and `search_stickers`, both run through `ToolRegistry.execute_tool` with context `chat_id=session.chat_id`, `requester_user_id=caller_id`. Any other tool name is rejected with an error result.
- **`ignore_user` reused as is:**
  - The session id is negative, so a chat-scoped ignore becomes an `ignore:<uid>` preprocessor on the guest session.
  - `scope="global"` bans everywhere. Evidence is the session `#id`s, which are real stored rows, so the evidence check passes.
  - Admins are exempt already.
- **Stickers:** build the HandleMap that `build_chat_messages` builds (pinned favorites plus relevant stickers, `prompt_builder.py:647`), with the stickers half only, no GIFs. Set `current_handle_map` so `search_stickers` handles resolve.

## 4. Answer: text or one sticker
- Collect `<msg>`/`<reply>` text and `<sticker id=…/>` events from all steps.
- If there is text, answer with an article as now. If there is only a sticker, answer with `InlineQueryResultCachedSticker`. If the sticker answer fails, fall back to the sticker's emoji as text.
- Save Nox's message as `media_kind="sticker"`, `media_file_id`, text `[Sticker: <caption>]`.
- Prompt: rewrite the guest instruction.
  - Your answer is ONE reply: text, or a single sticker and nothing else.
  - Tools here: `ignore_user` and `search_stickers`. Use `ignore_user` on abusive callers, as in BULLIES; `scope=global` bans for abuse only.
  - Remove "Tools … disabled".

## 5. Ignore gate (`guest.py`, before quota, next to the ban gate)
- If `PreprocessorStore.get_by_name(session.chat_id, "ignore:<caller_id>")` is enabled and not expired (same check as `engine.py:216`): no LLM call, no quota charge.
  - With an emoji set, the answer is that emoji.
  - With none, there is no answer.
- Both the ignore emoji and the existing ban notice/emoji go through `bans._claim_notice(session.chat_id, caller_id)`, because every guest answer posts a real message into that chat.

## 6. Config, docs, monitoring
- Add `guest_mode.max_tool_steps: 2` and `guest_mode.allow_stickers: true` to `src/config.py` and `config.yaml`. Document them in `docs/configuration.md` §2.18, including the ignore/ban behaviour.
- Studio needs no UI work:
  - guest ignores appear as preprocessors on the "… - GUEST" chat;
  - bans appear under User Quotas → Banned users;
  - sticker answers appear in the transcript as `[Sticker: …]`;
  - the tool calls are traced under the guest session.
- Plan folder `plans/2026-11-29-guest-mode-tools/` plus a row in the index.

## Tests (mine, `tests/test_guest_mode.py` extended, fake LLM)
- **Dedup:**
  - the replied-to text appears once in the prompt;
  - the current request is listed with its `#id`;
  - a reply to Nox's own rendered answer reuses the stored id, with no new row;
  - an already-described sticker is not run through vision again.
- **Tools:**
  - `ignore_user` creates `ignore:<caller>` on the session;
  - the next call from that caller makes no LLM call and charges no quota;
  - the emoji goes out once per cooldown;
  - no emoji means no answer;
  - expiry works;
  - an admin can't be ignored;
  - another caller in the same session still gets answers;
  - `scope=global` creates a ban;
  - a tool outside the allowlist is refused.
- **Sticker:**
  - only a sticker → the cached-sticker result with the right `file_id`, recorded as a sticker message;
  - text plus sticker → text wins;
  - a failed sticker answer → emoji text.
- **Limits:** steps are capped by `max_tool_steps`; one quota charge per call.

## Verification
1. Run the full NoxChat suite locally.
2. Commit NoxChat (only these files) and bump the MADTOM pointer.
3. Deploy to tp1 the standard way:
   - `git diff config.yaml` is empty;
   - run the worktree suite;
   - check no jobs are active;
   - pull, restart, check the logs;
   - update the deploy log.
4. You'll need to try a real guest call, since I can't start one myself. Check that the sticker reply and an `ignore_user` work live.
