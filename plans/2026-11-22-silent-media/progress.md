# Progress

- [x] GIF routing fix + tests (`test_gif_is_never_routed_to_audio`, `test_plain_video_document_still_routed_to_audio`)
- [x] `NoAudioStream` / `NoAudioJob` short-circuit + tests (`test_video_without_audio_track_skips_analysis`, `test_no_audio_description`, updated `test_prepare_audio_video_with_and_without_audio`)
- [x] Full suite: 623 passed; the 19 failures (doc/video sandbox jail tests) are pre-existing on the dev box and identical without the change
- [ ] Deployed to tp1
- [x] Deployed 16beb88 to tp1 (2026-11-22 02:48 JST); cancelled 22 backlog jobs
- [x] Phase 2 (NoxChat bbbcab6): auto_kinds gate, channel-post exclusion, skipped rows, per-chat limits, fair queue, Studio usage data; 21 tests in tests/test_audio_gate.py; tp1 suite 660 passed (known failures only + flaky test_workspace pdf_edit, passes on rerun); deployed 2026-11-22 03:09 JST
- [ ] MADTOM Studio UI: show `audio_chat_usage` / `audio_chat_limits` from get_active_jobs
