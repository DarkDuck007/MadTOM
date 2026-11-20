# Nox improvement review: performance, token usage, practicality

Date: 2026-11-15. Read-only review of `NoxChat/` at commit `3968264`. Nothing was changed, committed or deployed.

## How this was produced

Five reviewers each got the same brief and worked alone, without seeing each other's output. I ran my own pass in parallel, then checked their claims against the source and tp1's logs.

| Reviewer | Model | Time | Notes |
|---|---|---|---|
| Argon | agy `gemini-4-argon-high` | 13 min + 5 min follow-up | 22 suggestions; ran the tool registry to measure schema sizes |
| Hydrogen | agy `gemini-4-hydrogen-high` | 7 min | 20 suggestions; also measured schemas |
| Flash | agy `gemini-3.8-flash-high` | 11 min + 1 min follow-up | 20 suggestions |
| Astra | codex `gpt-6-astra` | 6 min | 25 suggestions |
| Sol | codex `gpt-6.1-sol` | 8 min | 25 suggestions |
| Claude | me | — | own pass, verification, tp1 log data |

- **Effort setting.** The three Gemini models ran their explicit `-high` variants. The delegate tool has no effort parameter for codex, so Astra and Sol were only asked for high effort in the brief; I cannot confirm what they ran at.
- **Discussion round.** I sent follow-up challenges to Argon and Flash where I found a claim to be wrong or where reviewers disagreed. Argon withdrew one suggestion and Flash withdrew one and downgraded two. Hydrogen, Astra and Sol got no follow-up; their claims that I checked held up.
- **Verification marks.** "Verified" means I read the cited code or log myself. "Not checked" means the claim comes from a reviewer and looked plausible but I did not open the code.
- **Left out on purpose.** Two reviewers raised items that are privacy or security findings (one about memory scope, one about the web page fetcher). Their details are not in this file; they belong in the private audit plan. See "Items moved to the private audit" at the end.

## Production numbers used in this review

Taken from tp1's logs for the last 24 hours (read-only):

- About 1,640 replies dispatched.
- 15 session rebases, all triggered by the token ceiling, none by the 64-turn limit.
- Sessions rebase at about 72,250 total tokens. The ceiling is `bootstrap_tokens + 36000`, so a fresh session starts at roughly 36,000 tokens.
- 90 idle session closes in the bridge and 0 "session expired" retries. Idle sessions are resumed without a new bootstrap.
- NoxChat logs no per-turn timing or token line, so where reply time goes is not measured today.

Measured sizes (Argon and Hydrogen ran the registry and agree to the character):

| Block | Characters | Rough tokens |
|---|---:|---:|
| Static system prompt (base + Nox persona) | 19,041 | 4,800 |
| of which `tool_rules` | 10,858 | 2,700 |
| of which `tag_rules` | 5,400 | 1,350 |
| Tool definitions as sent to agy (24 tools, indented JSON) | 32,342 | 8,100 |
| Seed history on a bootstrap (hard cap) | — | 10,000 |
| Document Studio prompt + `AGENT_TOOLS.md` | 41,243 | 10,300 |

## The main finding: the session prefix is paid on every turn

Most reviewers treated the tool definitions and static prompt as a cost paid "only on bootstrap". In stateful mode that is the wrong way to count. Whatever goes into the bootstrap stays in the session until the next rebase, and every later turn re-reads the whole session. Sessions live between about 36k and 72k tokens, so the average turn carries roughly 50k tokens of context.

So each 1,000 tokens removed from the bootstrap is removed from every turn of that session: about 1,640 turns a day. Trimming 6,000 tokens from the prefix is on the order of 10 million fewer (mostly cached) input tokens a day, and sessions also last longer before they hit the ceiling. This makes prefix trimming the highest-value token work, ahead of anything in the per-turn diff.

The tool definitions (8,100 tokens) are larger than the whole system prompt (4,800 tokens), and nobody had looked at them before this review.

## Top recommendations

In the order I would do them.

1. **Add a per-turn timing and token log line first.** Everything on latency below is an estimate until this exists. (Astra, Sol, me)
2. **Shrink the tool definitions sent at bootstrap** from about 8,100 to about 4,000 tokens. (Argon, Hydrogen, Astra, Sol, Flash)
3. **Trim `tool_rules` and `tag_rules` further**, and cap recalled memories much lower than 8,192 tokens. (all five)
4. **Reuse the SQLite connection and batch the per-candidate lookups.** (all five)
5. **Take embedding and vector searches off the event loop and run the independent lookups together.** (all five)
6. **Fix the small real bugs found along the way:** `/forget` with the short IDs it displays, bot messages saved as sender "Danial", guide reminders suppressed after a rebase, cooldown delaying explicit mentions. (various; all verified)
7. **Bound the total time of one reply.** Today one failing step can retry for about 15 minutes. (Astra, Sol)
8. **Give users direct job controls** (`/jobs`, `/cancel`) and a live progress message for studio jobs. (Argon, Hydrogen, Astra, Sol)

## 1. Token usage

### 1.1 Shrink the tool definitions
- **Raised by:** Argon (#1), Hydrogen (#2), Astra (#9), Sol (#10), Flash (#15). Unanimous.
- **Today (verified):** 24 tools are sent on the bootstrap turn through `format_tools_system_block` (`services/agy-openai-bridge/bridge/tool_protocol.py:16`) as indented JSON. The largest are `create_document` (4,121 chars), `create_image` (3,254), `search_chat_history` (2,625) and `add_sticker_to_library` (2,085). `add_stickers_to_library` is a second copy of the same tool (`src/media/tools.py:534`). Only the root `title` is stripped from the Pydantic schema (`src/tools/registry.py:68`).
- **Change:**
  - Send compact JSON without indentation.
  - Strip nested `title` and default metadata.
  - Drop the plural alias from the advertised list but keep accepting the name.
  - Cut the long descriptions of `create_document` and `create_image` down to a summary, since the full text is already in `tool_guide`.
- **Impact:** high. Argon estimates 32.3k → about 15.5k characters, around 4,000 tokens off every turn of every session.
- **Effort:** S–M.
- **Risk:** shorter descriptions can lower tool-call accuracy. Check with a few real chats before and after.
- **My verdict:** do it. This is the best ratio of saving to effort in the whole review.

### 1.2 Trim the base prompt again
- **Raised by:** all five.
- **Today (verified sizes):** `tool_rules` is still 10,858 characters after the earlier cut, and `tag_rules` is 5,400. Argon and Astra point out repeated `<remember>`/`<forget>`/`<react>` explanations inside `tag_rules`, and a rule in `nox.yaml` (silent indexing) that conflicts with the base rule to announce tool use. I did not check those two details line by line.
- **Change:** move the chat-history-search and sticker-tool detail into `_guides.yaml`, remove the repeats in `tag_rules`, settle which rule wins.
- **Impact:** medium–high: 1,100 to 2,000 tokens off every turn.
- **Effort:** S (text editing), plus a behaviour check.
- **My verdict:** do it together with 1.1 so behaviour is tested once.

### 1.3 Cap recalled and hot memory lower
- **Raised by:** Hydrogen (#13), Astra (#11), Sol (#13). Argon's bootstrap breakdown agrees these are the largest variable block.
- **Today (verified):** `max_memory_tokens: 8192`, `max_per_memory_tokens: 2096`, `top_k_memories: 10`, and hot memory up to 6,000 tokens (`config.yaml:95-100, 355-358`). Memory recall has no relevance floor, unlike tips and profiles (`src/memory/hybrid_search.py:147`, not opened by me).
- **Change:** start at about 2,500 tokens total and 500 per memory, add a minimum relevance, let Nox fetch a full memory with `search_memories` when needed.
- **Impact:** medium–high in chats with many memories; these blocks sit outside the 10k history cap, so they explain much of the gap between the 23k of static content and the 36k bootstrap.
- **Effort:** S for the config, M for the relevance floor.
- **Risk:** recall of rare facts drops; tune on real multilingual chats.
- **My verdict:** do the config change now and measure; add the floor after.

### 1.4 The rebase ceiling is above the configured context limit
- **Raised by:** Hydrogen (#9), Astra (#4), Sol (#8).
- **Today (verified in code and logs):** `should_rebase` uses `max(0.85 × max_context_tokens, bootstrap_tokens + 36000)` (`src/persona/session_tracker.py:114-117`). With `max_context_tokens: 65536` the logs show ceilings of 72,250 and one of 91,820. The setting therefore does not limit anything in stateful mode.
- **Change:** decide what the real limit is. Either raise `max_context_tokens` to what you accept, or make the ceiling respect it. A lower ceiling means smaller average context per turn but more rebases; with a slimmer bootstrap (1.1–1.3) a rebase becomes cheap, so a ceiling near bootstrap + 24k is worth trying.
- **Impact:** medium, and it sets the average context size, which is the main token cost.
- **Effort:** S.
- **My verdict:** do after 1.1–1.3, with the new log line to compare.

### 1.5 One image budget for a turn
- **Raised by:** Astra (#12), Sol (#14), Argon (#17), Hydrogen (#19), Flash (#17).
- **Today:** job images, PDF collages, photos, video grids and replied-to images each have their own cap and are appended one after another (`src/telegram/runtime.py:583-695`, not opened by me). A PDF can add 4 collages and a video 4 grids.
- **Change:** one total per turn, current request first, a text note for what was left out. Argon adds that 3×3 sticker grids at 768 px cross a tile boundary and could be 576 px; that depends on Gemini's image tokenisation and is unverified.
- **Impact:** medium, high on media-heavy turns.
- **Effort:** M.
- **My verdict:** worth doing; lower priority than 1.1–1.4.

### 1.6 Slim the Document Studio prompt
- **Raised by:** Hydrogen (#5), Argon (#10).
- **Today (verified size):** each doc job starts with 41k characters, of which `AGENT_TOOLS.md` is 29.6k.
- **Change:** include deck sections only for deck jobs and PDF sections only for PDF jobs.
- **Disagreement:** in the follow-up Argon rated this among its least certain: PDF edit jobs mix both toolsets, and the document was just rewritten so the studio stops probing the library.
- **My verdict:** not now. The last change to that file bought a real speed-up by being complete; splitting it risks bringing the probing back for a few thousand tokens per job.

### 1.7 Smaller items
- **Tool result boilerplate** (Flash #16): `search_chat_history` adds a 653-character instruction to every result and `web_search` 250. Not checked. Low effort, small saving, and in a session each result stays until rebase. Worth doing.
- **Bound tool results** (Astra #15): no central size limit on tool results kept in the turn (`src/telegram/dispatcher.py:1005`). Not checked. Medium value.
- **Sticker and GIF captions in the diff** (Argon #7): shorten per-candidate captions. Argon itself rated this uncertain, since slot churn is already capped at 2 stickers and 1 GIF per turn. Skip for now.

## 2. Performance and latency

No reviewer could measure reply latency, and neither could I, because nothing logs it. The millisecond figures below are the reviewers' estimates. The model call almost certainly dominates a reply, so expect the items in this section to save hundreds of milliseconds, not seconds, except 2.5 and 2.6.

### 2.1 Measure first
- **Raised by:** Astra (#5), Sol (#4), me.
- **Today (verified):**
  - There is no per-turn log line with stage timings.
  - The prompt-token figure sent to telemetry is `len(json.dumps(messages)) // 4` (`src/llm/client.py:159`), which counts base64 images as text and leaves out tool schemas.
  - `py.cpu.user_pct` divides cumulative user time by the interval (`src/control/telemetry.py:103`), so it grows without bound.
  - Sol adds that cached tokens are recorded as zero (`src/control/server.py:326`); I saw the literal `0` there.
- **Change:** one INFO line per reply: wait, media preparation, prompt build (embedding, retrieval), time to first visible bubble, total, steps, tokens in/out/cached as reported by the bridge, and whether the turn was explicit or ambient.
- **Effort:** S–M.
- **My verdict:** first thing to do; it decides the order of everything else here.

### 2.2 Reuse the SQLite connection, batch per-candidate reads
- **Raised by:** all five.
- **Today (verified):** `get_db()` opens a connection, runs four PRAGMAs and closes it on every call (`src/db/session.py:15-29`). Tips retrieval fetches up to 50 candidates and loads each with its own `TipRepository.get` (`src/tips/service.py:310-316`). Reviewers report the same pattern in memory RAG (`src/memory/hybrid_search.py:164`), not opened by me.
- **Change:** one long-lived connection (or a writer plus a small reader pool), and `get_many(ids)` queries.
- **Impact:** estimates range from 30 to 150 ms per turn. The tips loop costs nothing today because there are no tips yet; it will once there are.
- **Effort:** M for the connection, S for the batching.
- **Risk:** a shared connection needs clear transaction ownership.
- **My verdict:** do the batching now (it is small and the tips part is my own code); do the connection change with tests.

### 2.3 Embedding and vector search off the event loop, lookups in parallel
- **Raised by:** all five.
- **Today (verified):**
  - Tips embed their own query and finish before the main query embedding starts (`src/persona/prompt_builder.py:505-506, 584`).
  - The sticker and GIF vector searches are synchronous calls on the event loop (`prompt_builder.py:595, 621`).
  - Pins, preferences, profiles, memories and hot memory are awaited one after another.
  - Memory commits call `embed_passage` synchronously (`src/memory/extractor.py:156`, per Astra and Hydrogen, not opened by me).
- **Change:** embed the two queries as one batch (the two texts differ, so they cannot share one vector, as Sol and Astra correctly note against Argon and Hydrogen); run the independent reads with `asyncio.gather`; push Qdrant calls to a thread.
- **Impact:** medium per turn, higher when several chats are active, because a blocked loop stalls every chat.
- **Effort:** S–M.
- **My verdict:** do it.

### 2.4 Tool calls of one step in parallel
- **Raised by:** Hydrogen (#10), Flash (#5), Astra (#16), Sol (#16).
- **Today:** tool calls from one model step run in sequence (`src/telegram/dispatcher.py:994`).
- **Disagreement:** Flash and Hydrogen propose a plain `gather`. Astra and Sol warn that media searches write to the shared handle map and writes can depend on order, so only tools marked read-only should run together.
- **My verdict:** Astra and Sol are right. Add a read-only flag per tool and parallelise those. Medium value: it only helps turns where the model issues several lookups at once.

### 2.5 Reply timing: wait and cooldown
- **Raised by:** Flash (#9), Astra (#7), Sol (#12), all proposing a DM wait of 0.3–0.5 s and little or no cooldown.
- **Today (verified config):** `dm_wait_seconds: 2`, `group_wait_seconds: 3`, `cooldown_seconds: 5`.
- **Discussion:** I asked Flash to read the scheduler and say what breaks. Its answer:
  - Cooldown only delays messages that arrive within 5 s of the previous reply finishing.
  - Cooldown also holds back explicit mentions and replies in groups, since the deadline does not look at whether a message is explicit (`src/telegram/scheduler.py:146`).
  - A 0.5 s wait splits photo albums, splits "hey" + "question" bursts into two turns, and can answer a text before the voice note sent with it has been transcribed.
  - Flash then withdrew its own proposal.
- **My verdict:**
  - Keep the 2 s DM wait.
  - Make cooldown skip explicit triggers, or lower it to 1–2 s. That removes up to 5 s from quick back-and-forth, which is the case users feel.
  - If more is wanted later, shorten the wait only for a single text message with no media group and no pending audio.
- **Effort:** S.

### 2.6 Web search: stage deadlines and the second model call
- **Raised by:** Argon (#22), Sol (#17), Astra (#13), Flash (#10), Hydrogen (#14).
- **Today (verified config):** a tool has 20 s; search may take 10 s, page fetches 15 s, and then `llm_aggregation: true` calls the main model again to summarise before the main agent answers.
- **Agreement:** give the stages a shared budget and return what is ready when time runs out.
- **Disagreement:** four reviewers say turn the summarising call off and use the existing extractive brief. In the follow-up, Argon made the opposite point: the extractive brief is up to 6,000 characters of scraped lines, and in a stateful session that stays in context until the rebase, while the summary is 1,500–3,500 characters and cleaner.
- **My verdict:** fix the deadlines now. Keep the summary on for now, and decide with the timing log: if search turns are slow because of that call, switch to extractive with a lower `aggregation_budget_chars`.

### 2.7 Bound the total time of one reply
- **Raised by:** Astra (#3), Sol (#2).
- **Today (verified):** `LLMClient` retries up to 10 times (`src/llm/client.py:86`), each attempt with its own 90 s step timeout (`:216`). One failing step can take about 15 minutes, and a turn can have 10 steps. The chat's queue is blocked meanwhile.
- **Change:** one deadline for the whole reply, fewer retries, and a visible failure notice for explicit requests (Flash #19 asks for the notice too).
- **Impact:** high for the bad days, none for normal ones.
- **Effort:** M.
- **My verdict:** do it. The night of slow API responses that caused the studio timeouts would have hit chat replies the same way.

### 2.8 Smaller items
- **Image work on the event loop** (Hydrogen #11, Sol #15, Argon #11): photo resize, TGS rendering and JPEG encoding run inline, and several attachments are processed in sequence (`src/telegram/media.py:493-558`). Not checked. Medium; do with 2.3.
- **Sticker indexing blocks the loop** (Argon #9): per-item embedding in a loop during pack indexing; Flash (#12) says the same for the startup resync. Not checked. Medium; these freeze all chats for seconds while they run.
- **Startup** (Hydrogen #17, Astra #24, Sol #22): polling starts only after all recovery and the tips store are ready, and the embedding model loads on first use. Not checked. Low–medium; matters after each deploy.
- **Serialising the request for telemetry** (Hydrogen #7, Argon #13): Hydrogen says the full request, including base64 images, is serialised even when capture is off. I confirmed the `json.dumps(messages)` token estimate on one path and that `emit_api_call` returns early when capture is off; I did not confirm that the serialisation happens before that check on every path. Cheap to fix if so.
- **Unconditional chat-title write on every message** (Argon #16), **quota counts run twice per turn** (Flash #11), **`is_file()` per workspace item** (Flash #20), **token recount on every loop pass when trimming history** (Hydrogen #9, Argon #12): all small and unverified; pick up when touching those files.
- **Reuse HTTP clients** (Argon #15, Sol #21, Hydrogen #14): low value against a local bridge.
- **Merge the three Qdrant directories** (Flash #14): withdrawn by Flash in the follow-up; the RAM saving was a guess and the migration would orphan existing vectors. Skip.

## 3. Practicality: features, reliability, operations

### 3.1 Bugs found (all verified)
- **`/forget` does not accept the IDs that `/memories` shows.** `/memories` prints the first 8 characters of the ID (`src/telegram/commands.py:305`); `/forget` matches the full ID only, then falls back to a keyword search (`:324`). Nox's own `<forget>` tag does prefix matching and also removes the vector (`src/telegram/dispatcher.py:1186-1209`). Found by Flash. Fix: reuse the same routine. Effort S.
- **Bot messages are stored with sender "Danial" / "nox"** at 8 places in `src/telegram/dispatcher.py`, whatever persona is active. Found by Argon, Hydrogen, Flash. It affects sender filters in `search_chat_history` and the Studio's history view. Fix: take the name from the active persona; Argon also suggests marking bot rows in search results. Effort S; a one-time update of old rows is optional.
- **Guide reminders stay suppressed after a rebase.** `tool_guide` remembers "seen" per chat for 6 hours (`src/persona/guides.py:23-26`), but a rebase or `/clear` removes the guide text from the session, about 15 times a day. Sending the reminder also marks the guide as seen (`:107`). Found by Hydrogen, Astra; Argon agreed strongly. Fix: clear the entries in `reset_chat` and on rebase. Effort S. This one is in code I wrote in the last round.
- **Cooldown delays explicit mentions in groups** (see 2.5).
- **Settings that do nothing:**
  - `llm.session_idle_timeout_seconds` is never read in `src/` (Argon, Sol).
  - `embeddings.batch_size` is never passed to the embedder (Astra, Sol).
  - `temperature` and `max_output_tokens` are sent by NoxChat but the bridge does not forward them to agy; a grep of the bridge finds neither (Astra).
  - Fix: wire them up or mark them as unused in `config.yaml` and the docs.
- **Docs:** `docs/tips.md` describes passage embedding for stored tips, while the code deliberately uses the query adapter on both sides (Sol). The doc should be corrected.

### 3.2 Direct job controls and visible progress
- **Raised by:** Argon (#19), Hydrogen (#16), Astra (#23), Sol (#25).
- **Today:** status, cancel and revise exist only as tools for Nox. If nobody talks to Nox during a 5-minute job, the chat sees nothing; `/clear` does not cancel studio jobs.
- **Change:** `/jobs` and `/cancel <id>` for the requester and admins, and one status message per job that is edited at milestones and replaced on delivery. No model turns involved.
- **Impact:** medium–high for daily use; also works while the model API is down.
- **Effort:** M.
- **My verdict:** the most useful feature suggestion in the set.

### 3.3 Session state does not survive a restart
- **Raised by:** Hydrogen (#4), Astra (#17 in part); Argon was asked and agreed.
- **Today (verified):** the session tracker lives in memory. Every NoxChat restart makes every active chat start with a new 36k-token bootstrap, and the old agy conversation is abandoned. Each deploy costs this.
- **Change:** store the small per-chat state (conversation id, epoch, turn index, last sent message id, seen hashes) in SQLite at commit and load it on first use.
- **Impact:** medium; scales with how often the service restarts.
- **Effort:** M.
- **My verdict:** worth doing after the prefix is slimmer; less urgent once a bootstrap is cheaper.

### 3.4 Removals do not reach a running session
- **Raised by:** Astra (#6), Sol (#11).
- **Today:** the context diff sends new and changed memories and tips but nothing when one is deleted, disabled or unpinned (`src/persona/session_tracker.py:246, 289`). A tip you delete in the Studio keeps steering that chat until its next rebase.
- **Change:** send a short "no longer applies" line for entries that were shown and have been removed.
- **Impact:** medium; matters most for tips, where removal is how you correct behaviour.
- **Effort:** M.
- **My verdict:** do it for tips first. I verified the tips part (it is the step I added to `prepare_turn`); the memory and pin parts are not checked.

### 3.5 A failed prompt build loses the message silently
- **Raised by:** Sol (#3), Astra (#3), Flash (#19).
- **Today:** the coordinator removes the batch before `respond()` and only logs an exception (`src/telegram/scheduler.py:151-160`); prompt building runs before the handler that sends the failure reply (`src/telegram/runtime.py:740-748`). Not opened by me beyond the retry block.
- **Change:** put preparation inside the same error handling, let optional retrieval fail soft, and send a short notice on explicit requests.
- **Effort:** S–M.
- **My verdict:** do with 2.7.

### 3.6 Ambient turns
- **Raised by:** me; Argon was asked.
- **Today (verified):** in groups a full model turn runs every 20 messages (`auto_reply_interval: 20`, `src/telegram/runtime.py:365-367`) so Nox can decide whether to chime in. How many of the 1,640 daily replies start this way is unknown because it is not logged.
- **Argon's view:** a pre-filter is not worth it; with a cached prefix an ambient turn that ends in silence is cheap.
- **My verdict:** log the count first (2.1). If ambient turns are a large share and mostly end silent, raise the interval per chat before building any filter.

### 3.7 Other suggestions
- **Deterministic routes for simple edits** (Astra #19, Sol #18): crop, resize, rotate, PDF merge or extract without a studio session, as `convert_video` already does. Saves a whole studio run (tens of seconds) for trivial requests. Effort M. Good idea; second-best feature suggestion.
- **Tip moderation from Telegram** (Flash #13): `/tips pending|approve|reject` for the admin, so proposals from Nox can be handled without the desktop app. Effort M. Useful if proposals become frequent.
- **Richer `/status` and `/usage`** (Hydrogen #15): bridge health, session turn and tokens, active jobs, workspace use, image and document quotas. Effort S.
- **Reuse the studio's preview collage on delivery** (Astra #20, Sol #20): NoxChat re-renders collages that the studio already made. Not checked. Medium.
- **Skip memory extraction on trivial chunks** (Argon #18): sticker-only or very short messages still count toward the 16-message extraction chunk. Not checked. Fewer background model calls.
- **Storage warnings** (Hydrogen #20): warn the admin at about 85% of the workspace cap instead of failing jobs when full. Effort S.
- **Config handling** (Astra #25, Sol #24): a YAML validation error makes the bot fall back to defaults silently (`src/config.py:539-563`, not opened by me). A loud failure would be safer. Effort M.
- **Version the vector collections by embedding model** (Sol #23): only tips check dimension and distance at start. Changing `embeddings.model_name` would silently mix vectors in memories and media. Effort M; do before any model change.
- **Duplicate RPC handlers and stale `src/persona/persona.yaml`** (Hydrogen #18): cleanup, not checked.
- **Fair queueing of studio jobs across chats** (Astra #21, Sol #19): relevant only under bursts; not needed at current load.

## Where reviewers were wrong or withdrew

- **Argon #14** (idle timeout forces a re-bootstrap): withdrawn after my challenge. Idle sessions are resumed; the logs show 90 idle closes and no expirations.
- **Flash #6** ("history mode is not set, the bot runs stateless"): withdrawn. `config.yaml` sets `history-mode: "agy"`; Flash searched only for the underscore spelling.
- **Flash #10** (pin the aggregation model to Flash Low): that is already the main model.
- **Flash #9 and #14**: downgraded by Flash itself (see 2.5 and 2.8).
- **Argon #2 and Hydrogen #1** (compute one embedding and share it with tips): the two query texts are different by design, so they can be batched but not shared.
- **Impact numbers:** every latency figure in the reports is an estimate; none was measured.

## Already good; leave alone

Named by at least three reviewers, and I agree:

- The stateful context diff with staged commit, and tool definitions sent only at bootstrap.
- Idle sessions resuming through the bridge without a new bootstrap (confirmed in production logs).
- The debounced single response stream per chat and the merged tool-status bubble.
- The literal → fuzzy → semantic retrieval order.
- On-demand tool guides (the mechanism; see 3.1 for the reminder bug).
- Document job state transitions, steering and extension, and recovery after restart.
- The studio jails and chat-scoped workspace.
- Replacing an image with its caption in later turns.

## Items moved to the private audit

Two findings are about privacy or security and their details are deliberately not in this file:

1. Sol's first-ranked item, about which memories certain entry points can read or invalidate. I read the two cited places and the concern looks real.
2. Hydrogen's item about a request filter in the web page fetcher that is defined but may not be attached. Not checked by me.

Both should be added to `~/.claude/plans/noxchat-open-source-cleanup.md` and verified there.

## Suggested order of work

1. Timing and token log line (2.1).
2. Prefix diet: tool definitions, base prompt, memory caps (1.1–1.3), then revisit the rebase ceiling (1.4).
3. Small bug fixes (3.1) and the cooldown change (2.5).
4. Database batching and connection reuse, event-loop offloading, parallel lookups (2.2, 2.3).
5. Reply deadline and failure notice (2.7, 3.5).
6. Job controls and progress message (3.2).
7. Then, guided by the new log data: web search (2.6), ambient turns (3.6), session persistence (3.3), removals in sessions (3.4), deterministic edit routes (3.7).

The reviewers' full reports are in `~/.local/state/claude-delegate/jobs/<id>/job.json` (`report` field): Argon `cad3ae1e`, Hydrogen `c57aad89`, Flash `6f0addf9`, Astra `4d9a7e9b`, Sol `db733900`.
