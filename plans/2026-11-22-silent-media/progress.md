# Progress

- [x] GIF routing fix + tests (`test_gif_is_never_routed_to_audio`, `test_plain_video_document_still_routed_to_audio`)
- [x] `NoAudioStream` / `NoAudioJob` short-circuit + tests (`test_video_without_audio_track_skips_analysis`, `test_no_audio_description`, updated `test_prepare_audio_video_with_and_without_audio`)
- [x] Full suite: 623 passed; the 19 failures (doc/video sandbox jail tests) are pre-existing on the dev box and identical without the change
- [ ] Deployed to tp1
