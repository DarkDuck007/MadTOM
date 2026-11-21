# Skip audio processing for GIFs and silent videos

Approved 2026-11-22 (options 1 and 2; the partial-download probe was dropped because tagged videos
must be fully downloaded anyway for the visual pass).

## Root cause
- Telegram GIFs arrive as `animation` **and** a backward-compat `document` (mime `video/mp4`).
  `find_media_attachment` (NoxChat `src/telegram/media.py`) checked the document fallback in its audio branch,
  so every GIF was returned as `"video"` and queued in `AudioService`.
- Real videos without an audio stream were downloaded, probed, then a fake 1 s silent WAV was pushed through
  classifier/song/STT and described as `[Silence / inaudible audio]`.

## Changes
1. Routing: the audio branch of `find_media_attachment` skips any candidate that has `animation`; GIFs go to the
   vision path (`process_animation`).
2. No-audio short-circuit: `prepare_audio` raises `NoAudioStream` when ffprobe finds no audio stream.
   `AudioService._prepare` turns it into `NoAudioJob` carrying an `AudioAnalysis(no_audio=True, status "no_audio")`;
   `_run` completes and delivers it without classifier/song/STT. `describe()` -> `[No audio track — this media has no sound]`.
3. `audio.processing_revision` bumped to `v2` so cached v1 "silence" results for track-less videos are not reused.

Videos with an audio stream that is digitally silent still go through the existing `silent` path.
