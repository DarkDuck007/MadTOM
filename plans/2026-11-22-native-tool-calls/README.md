# "Studio not working": rejected native tool calls; sandbox media tools

## What users saw

Nox said its Image Studio, Document Studio or sandbox was broken ("مثل اینکه استودیو تصویرم الان قاطی کرده"), then
never retried. The studios themselves worked: the same GIF-subtitle job succeeded minutes later in a fresh session.

## Root cause

The bridge advertises NoxChat's tools to agy as text (an `AVAILABLE TOOLS` block on the bootstrap turn) and parses
`<tool_call>` text back out. agy itself knows none of them. Now and then the model emits a **native** function call
instead (almost always right after a `[TOOL RESULT]` turn). agy rejects it with `unknown tool: "create_image"`, the
model sees that error and tells the user the tool is broken. ACP does not report rejected calls, so the bridge never
saw them. agy's transcripts on tp1 held 14 such rejections across 8 sessions (create_image, create_document,
sandbox_run, view_pdf_pages, tool_guide, search_chat_history).

The only record is agy's per-session transcript:
`~/.gemini/antigravity-cli/brain/<session id>/.system_generated/logs/transcript_full.jsonl`. It holds the
`PLANNER_RESPONSE` step with `tool_calls` (name + args) and a `GENERIC`/`INVALID` step with the error. Steps are
written when they complete, i.e. before the model starts the follow-up response.

## Fix (agy-openai-bridge)

1. **Recovery.** `bridge/native_calls.py` `TranscriptWatcher` tails the transcript during an ACP turn. It polls
   on every ACP update (thought chunks and usage updates are forwarded as ticks for this) and before each text delta.
   An `INVALID unknown tool "X"` step where X is one of the request's tools turns the matching planner `tool_calls`
   entry into an OpenAI tool call. The turn is then cancelled and the call returned to NoxChat (streaming and
   non-streaming), so the "my tool is broken" text never reaches the chat. A rejection seen only at the very end of
   a turn is still returned. Native calls to tools the client never offered are left alone.
2. **Prevention.** The bootstrap tools block says the tools are not native functions; every later turn with tools
   carries a one-line reminder of the `<tool_call>` format.

## Sandbox media tools

The sandbox had no ffmpeg and no video libraries, so Nox could not fall back to it either.

- Base image (`services/nox-sandbox-image/build.sh`): apt `ffmpeg`, `gifsicle`, `libraqm0`, `libfribidi0`
  (Pillow complex text layout for Persian); pip `opencv-python-headless`, `imageio`, `imageio-ffmpeg`, `av`,
  `moviepy`, `pydub`, `arabic-reshaper`, `python-bidi` (all wheels on cp313). The build gate imports them, checks
  `PIL.features.check("raqm")` and encodes a test clip with libx264.
- Exports: `sandbox.export_allowed_exts` and `max_export_bytes` are now explicit in `NoxChat/config.yaml`. Both
  NoxChat and the nox-sandbox daemon read them. Before this, the daemon used its own shorter default list, so
  exports such as `.tex` or `.js` were silently refused. Media extensions were added.
- Sending: `<document id="FILE-…">` sends a workspace mp4/mov/webm as a video, a gif as an animation, and audio as
  audio. `kind="file"` forces a plain file and `kind="animation"` sends a silent mp4 as a GIF. The host still never
  parses sandbox output: Telegram does, and a rejected upload falls back to `send_document`.
