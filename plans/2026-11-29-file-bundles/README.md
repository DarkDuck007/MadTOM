# File bundles + no more silently dropped tool calls

## Incident (2026-11-28, DM 5957169442)
- Hel sent 105 lecture-slide photos in four waves (18:18–18:21 UTC) and asked for one clean PDF of all of them.
- Each wave became its own turn: every image went to vision and Nox captioned each one in its reply (~3k output
  tokens per wave). The session reached 91k tokens (ceiling 55.7k) and was rebased, so Nox only found 65 photos.
- `create_document` refused 65 inputs (cap 8).
- Four `sandbox_run` calls never ran. The model wrote `<tool_call>{…}}` without `</tool_call>`, and the bridge
  regex only matches closed blocks, so the call was dropped without a log line. The streamed text was held back,
  so NoxChat saw an empty step, sent the final-synthesis prompt, and Nox invented "it's rendering" and "it's in
  the background". No VM ever started.

## Fixes
1. **Bridge** (`services/agy-openai-bridge/bridge/tool_protocol.py`, `app.py`):
   - accept a last `<tool_call>` that is missing only its closing tag, when its JSON parses on its own;
   - when `<tool_call>` text still can't be parsed, log a warning and return `tool_call_error` in the response
     (final SSE chunk / completion body).
2. **NoxChat dispatcher**: a step with `tool_call_error` gets "your tool call was malformed and DID NOT RUN,
   re-issue it" instead of the synthesis prompt (`agent.malformed_tool_call_retries`, default 2).
3. **File bundles** (`src/telegram/bundles.py`, config `file_bundles`, docs §2.16):
   - photos/documents are held for `quiet_seconds`; a burst of `min_files` opens a bundle; it closes on
     `open_quiet_seconds` of silence, on the sender's next text (which becomes the request), or at
     `max_wait_seconds` / `max_files`;
   - closing builds a ZIP (`manifest.json` included) and numbered contact sheets, records
     `file_bundles` / `file_bundle_items`, and folds the members into one history line;
   - one turn: the instructions plus `initial_sheets` contact sheets; `sandbox_run(inputs=['BUNDLE-…'])` gets the
     ZIP; the `file_bundle` tool does list / info / view / sheet / send;
   - Studio: bundles are listed in Chat Files (store `bundle`, kind `zip`) and can be deleted there.
     Turns and tool calls are traced as usual.
4. **Repair of the broken DM**: `scripts/bundle_existing_messages.py` folds #31786–#31896 into a bundle and writes
   a one-off nudge (`<data_dir>/nudges/`, delivered on the next start) asking Nox to build the PDF from the ZIP
   in the sandbox.

## Not done
- The Telegram Bot API doesn't deliver the user's "uploading…" chat action to bots, so bundles close on quiet
  periods and text instead.
- The document studio still caps inputs at 8. A bundle is meant to be worked on in the sandbox.
