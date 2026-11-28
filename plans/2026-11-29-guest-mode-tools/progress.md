# Progress

- [x] Context dedup: request/reply are history rows, tail points at ids; rendered-text and media matching
- [x] Media viewing: photo/sticker/GIF/video processors on the summoning and replied-to message, once per session
- [x] Guest tool loop (`src/telegram/guest_turn.py`): ignore_user, search_stickers, search_gifs; `max_tool_steps`
- [x] One answer: text, or a cached sticker / mpeg4_gif (GIFs added at the user's request), emoji fallback
- [x] Ignore gate + ban/ignore answers on the notice cooldown
- [x] Config + docs; tests `tests/test_guest_tools.py` (13), full suite 871 passed, 2 skipped
- [x] Deployed to tp1 2026-11-29 00:39 JST (`bb32cfa`). [ ] Live check of a sticker answer and ignore_user (needs a real guest call)

## Notes
- Deviation from the plan: no stored `media_description` reuse (guest media has no description step); instead a
  replied-to message already in the session is not viewed again.
