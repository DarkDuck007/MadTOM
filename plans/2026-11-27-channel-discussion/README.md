# NoxChat: channel discussion groups (2026-11-27)

Goal: Nox should know when a group is a channel's comment section. Commenters who are not group members only
see the comment thread of the post they opened, so they see Nox's `<reply>` to them but never a plain `<msg>`.

## Bot API facts used
- `getChat(supergroup)` → `linked_chat_id` (the channel) and `join_to_send_messages` (False = non-members can comment).
- `getChatMember` → `left`/`kicked` for a commenter outside the group; `member`/`administrator`/`creator`
  (or `restricted` + `is_member`) for members.
- Channel posts arrive as automatic forwards from user 777000 (`is_automatic_forward`, `sender_chat` = channel).

## Done
- [x] `src/telegram/discussion.py`: cached lookups (TTL in `telegram.discussion`), chat-context hint, hidden-follow-up detection + note.
- [x] Ingest stores `messages.sender_is_member` (discussion groups only); transcript tags members `[member]`.
- [x] CURRENT CHAT CONTEXT gets a "Channel discussion" bullet (open-commenting variant explains `[member]` and one-`<reply>` rule).
- [x] `edit_message` tool (own text messages only; deferred tool).
- [x] After a turn: reply to a non-member (or to the channel post) followed by plain `<msg>`s → system note naming the
      hidden ids; up to `visibility_fix_max_steps` model calls with only `edit_message`/`delete_message`; text output discarded.
- [x] `thread_followups` (default on): plain `<msg>`s after a reply to a non-member/the post are sent as replies chained
      under Nox's previous bubble, so they stay in the commenter's thread; a reply to a member ends the chain.
      The hidden-follow-up fix-up stays as the fallback (only counts plain bubbles directly below the reply).
- [x] Config + docs/configuration.md; tests `tests/test_discussion.py` (15).

## Studio round (2026-11-28, agy gemini-4-neon-medium x2)
- [x] Parser: root cause = NoxChat 6d9e4c9 added ` UTC` to `[YYYY/MM/DD HH:MM:SS UTC]`; `ChatEntryHeaderRegex` did not
      accept it, so rebuilt histories became one text blob. Also `[member]` tag (IsMember + MEMBER badge), escaped `\n`,
      persona-name bot detection, IsRtl cached. My tests: `NoxChatRebuildParsingTests` (5).
- [x] Chat view perf: virtualized ListBox (was ItemsControl+StackPanel in a ScrollViewer), batched collection updates
      (1 Reset per switch, 1 range insert per older page), LRU chat view cache (8), newest-first detail waves, trace model
      building off the UI thread, bottom anchoring. My tests: `ChatViewPerformanceTests` (3).
- Pre-existing Studio failures at HEAD (not ours): SandboxTabTests.Preview_PagesAndOversizeRefusal,
  ChatFilesTabTests.Video_NoxAnimDecoding_ParsesFramesAndInterval.

## Tests memory (2026-11-28)
- [x] Every NoxChat test got the real jina-embeddings-v3 model (~7 GB RSS, 13.4 GB peak in a full run). Now
      `tests/fake_embeddings.py` (hashed trigram vectors) is the default; `@pytest.mark.real_embeddings` opts in.

## Open
- [ ] Rollout at midnight (agy sessions cold); noxai/noxchat NOT restarted yet.
- [x] Studio: `discussion_hidden_followups` shows as a generic trace row in the chat timeline (no kind filter).
- [ ] Live check on tp1 in a real discussion group (non-member commenter; verify `getChatMember` says `left`).
- [ ] Warm agy sessions only see the new hint after a session rebuild.
