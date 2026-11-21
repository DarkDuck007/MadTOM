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

## Phase 2 — audio only when asked (approved 2026-11-22)

Incident: 18 songs auto-forwarded from a linked channel (sender 777000, `is_automatic_forward`) into its
discussion group were each queued for classifier + song search + STT with nobody tagging Nox. Cause: the gate in
`runtime.accept()` was `kind in (voice, audio, video, video_note) and (not replied or explicit or ambient)`, so
*every directly attached* audio/video was queued. The 22 remaining jobs were cancelled by hand
(backup `data/audio_jobs_cancelled_1795283682.json` on tp1).

Rules:
- `audio.auto_kinds` (default `[voice, video_note]`): processed without being asked (transcribed; Nox is only called by
  a spoken nickname or the normal triggers, as for voice notes today).
- Everything else (audio files, videos, audio/video documents) is queued only when explicit: caption names Nox,
  reply to Nox, a tag replying to the media, a DM, or Nox calling `transcribe_audio`.
- Channel posts never auto-process (`is_automatic_forward`, sender_chat of type channel, or user 777000).
- Media not analysed is stored as an `audio_jobs` row in state `skipped` (so `transcribe_audio` / a later tag can
  run it) and the message text says `[... ] [Not analysed ...]`.
- Per-chat limits `audio.chat_limits` (shared by all kinds, admins exempt): `max_queued_jobs`, `max_jobs_per_hour`,
  `max_audio_minutes_per_hour`. Over the limit -> row stays `skipped`, Nox sees `[Audio unavailable: chat audio limit ...]`.
- Fair queue: the worker takes the next job from the least recently served chat.
- Studio: `get_active_jobs` returns `audio_chat_usage` (per chat, last hour).
