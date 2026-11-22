# Graph Report - NoxChat  (2026-11-21)

## Corpus Check
- 274 files · ~434,234 words
- Verdict: corpus is large enough that graph structure adds value.
- Unclassified: 9 file(s) not represented in the graph (top: (none) 3, .ini 2, .example 1)

## Summary
- 5754 nodes · 17900 edges · 217 communities (186 shown, 31 thin omitted)
- Extraction: 91% EXTRACTED · 9% INFERRED · 0% AMBIGUOUS · INFERRED: 1638 edges (avg confidence: 0.9)
- Token cost: 393,126 input · 0 output

## Community Hubs (Navigation)
- Memory Embeddings & RAG
- Chat Image Store & Inputs
- Chat File Store
- DB Repository & FTS
- Doc Tools MCP Server
- Prompt Tips Service
- Video Encoding Jail
- Media Repository & Usage
- Doc VM Exec Transport
- Sticker Analyst Map-Reduce
- Bridge FastAPI App
- Telegram Media Frames
- noxdoc Deck Builder
- Bridge Doc Studio Service
- Streaming Tag Parser
- ACP Session Pool
- Doc Job Inputs
- Doc Studio Quotas & Tests
- Dispatcher & Bubble Coalescer
- Prompt Builder & Handles
- Sandbox VM Agent
- Doc Store & DB Session
- Preprocessor Cron Tests
- Session Tracker & Token Budget
- User Profile Service
- Control RPC Handlers
- Agent Tool Registration
- Response Scheduler
- Chat Member Resolution
- Image Job Service
- Hybrid Retrieval Ranker
- Bridge Image Studio
- noxdoc Inspect Tasks
- Tips/Media/Log Search Docs
- noxdoc PDF Edit
- Doc Studio Bridge Tests
- Doc Tools Tests
- noxdoc Deck Edit
- Chat Quota Service
- Agent Turn Dispatch
- Doc Studio Contracts
- Chat Inspection & Trace
- noxdoc Fonts & Lint
- Workspace Manifest
- Persona Registry
- Spike Driver & Worker Pool
- App Config Models
- Telegram Runtime
- Cron Service
- noxdoc OOXML Charts
- Config: Embeddings & Memory
- Commands & Nicknames
- Memory Explorer CLI
- Sandbox HTTP Client
- Control Bridge Server
- Persona Guides & Audio Tools
- noxdoc Reports & Themes
- Doc Job Service Core
- Audio Benchmark Script
- Config: Audio
- Image Tools Workspace Tests
- Audio Job Repository
- Preprocessor Automation RPC
- agy Warm Worker Pool
- Sandbox Tools & Disk Index
- Guest Mode Bot
- Preprocessor Engine
- Video Studio Tool Tests
- Sandbox Agent Tests
- Sandbox Policy
- Config: Media
- SearXNG Client
- Chat Membership Events
- Agents & MCP Docs
- LLM Client
- Image Tools MCP Server
- Config: Doc Gen
- Doc Job Lifecycle
- PDF Render & Vision
- Control Direct Chat
- Qdrant Client Pool
- Preprocessor Media Handles
- Videokit FFmpeg
- Web Page Fetcher
- Config: Stickers
- Search Endpoints
- Message References
- Config: Search
- Image Tools Clip Ops
- Image Tools Job Context
- Audio Preparation
- Search Aggregator
- Content Sanitizer
- Cron & Preprocess Config
- Media Vector Store
- Image Tools Atomic IO
- Image Tools bwrap Jail
- Local Tool Registry
- Image Studio Deployment Docs
- Sandbox Client Test Server
- Message Deletion Tools
- Message Events & Search Shutdown
- Architecture Docs
- noxdoc CLI
- Doc & Image Quotas
- Doc Job Sandbox Delivery
- Control Files RPC Tests
- Config: Image Gen
- Doc Studio Workflow Docs
- PDF Edit Unreadable Handling
- Preprocessor Limits
- PDF Documents Store
- Studio SSE Client
- Forced STT Differentiation
- Sandbox Library Requests
- Fake Sandbox Client
- Commands & README
- Doc Studio Prompt Template
- Speech-to-Text
- Segment Search & RRF Plan
- Chat Workspace Docs
- Info Score & Context Budget
- Config: Telegram
- Image Studio Agent
- Sandbox Errors
- Search Tool Singletons
- Response Triggers
- Config: Context Budget
- Config: User Limits
- Config: Tips
- noxdoc Icons
- Initial Master Plan
- Bridge Workspace Checks
- noxdoc Image Helpers
- Sandbox Image README
- Config: LLM
- Audio & Reply Timing Docs
- Doc Studio Settings
- noxdoc SVG Art
- Log Search Plan
- Deck/PDF Edit Docs
- Config: Chat Search
- Config: Sandbox VM
- Config: Video
- Config: Retrieval
- Runtime Telemetry
- Info Scorer Training
- Persona: Minthara
- Persona: Nox
- Doc Studio Turn Timing
- Config: Control Center
- Config: Rate Limit
- DB Backup Script
- PDF Overlay Drawing
- Default Persona YAML
- Config: Agent
- Config: GIFs
- Config: Workspace
- Search Config Validation
- ACP API Error Filter
- Report HTML Template
- Config: Storage
- Config: Guest Mode
- Guide: Workspace
- Bridge Requirements
- Doc Studio Agent Variants
- PDF Vision Fixtures
- Config: App
- Config: Cron
- Config: Sandbox Exec
- Config: Memory
- Config: Profile
- Test Conftest
- Guide: Chat History
- Guide: Cron
- Guide: Document Studio
- Guide: Image Studio
- Guide: Preprocessors
- Guide: Sandbox
- Guide: Stickers
- Font Availability
- Sticker Analyst Prompts
- Audio Limited File
- Deck XML Tests
- Subprocess Cleanup Test
- Fake Embedding Runner
- Config: AI Limits
- Base Persona Rules
- Guide: Audio
- Guide: Files
- Guide: Messages
- Guide: Profiles
- Guide: Rich Messages
- Guide: Tips
- Guide: Web Search
- Audio Requirements
- ACP RPC Error
- Sandbox Build Script
- Kernel Fetch Script
- Doc Job Service Accessor
- PDF Download Failure Tests
- Lucide Icon Bundle
- Spike run_all Script
- Spike Setup Script
- SearXNG Instance Lists
- SearXNG Endpoints YAML
- noxchat Agent Definition
- Doc Tools Limits

## God Nodes (most connected - your core abstractions)
1. `get_db()` - 292 edges
2. `MessageRepository` - 124 edges
3. `MediaRepository` - 92 edges
4. `MessageRecord` - 89 edges
5. `init_db()` - 89 edges
6. `ToolRegistry` - 89 edges
7. `ChatImageStore` - 83 edges
8. `ActionDispatcher` - 80 edges
9. `DocJobService` - 74 edges
10. `TelegramRuntime` - 74 edges

## Surprising Connections (you probably didn't know these)
- `_write()` --indirect_call--> `p()`  [INFERRED]
  src/media/workspace.py → docs/doc-studio-progress/spike-driver/spike.py
- `_sync_fetch()` --indirect_call--> `code()`  [INFERRED]
  src/telegram/references.py → tests/test_preprocess_cron.py
- `_read_and_render_text_card()` --indirect_call--> `p()`  [INFERRED]
  src/control/rpc_handlers.py → docs/doc-studio-progress/spike-driver/spike.py
- `_read_text()` --indirect_call--> `p()`  [INFERRED]
  src/control/rpc_handlers.py → docs/doc-studio-progress/spike-driver/spike.py
- `_render_image_thumb()` --indirect_call--> `p()`  [INFERRED]
  src/control/rpc_handlers.py → docs/doc-studio-progress/spike-driver/spike.py

## Import Cycles
- 2-file cycle: `services/nox-doc-tools/noxdoc/__init__.py -> services/nox-doc-tools/noxdoc/deck.py -> services/nox-doc-tools/noxdoc/__init__.py`

## Hyperedges (group relationships)
- **Filtered segment embedding pipeline (score -> keep -> grow -> gate -> embed)** — message_info_score_and_segment_embedding_plan_infoscorer, message_info_score_and_segment_embedding_plan_contextual_keep_decision, message_info_score_and_segment_embedding_plan_segment_growth, message_info_score_and_segment_embedding_plan_quality_gate, message_info_score_and_segment_embedding_plan_segmentembedder, message_info_score_and_segment_embedding_plan_embedgate, message_info_score_and_segment_embedding_plan_chat_segments_collection [EXTRACTED 1.00]
- **Retained log search flow (spool -> catalog -> scan -> RPC -> Studio)** — new_search_plan_probe_spool, new_search_plan_retained_frame_catalog, new_search_plan_bounded_log_scan, new_search_plan_searchlogs_rpc, new_search_plan_search_cursor, new_search_plan_rawlogsapitabviewmodel [EXTRACTED 1.00]
- **NoxChat core principles from Mastermind lessons** — initial_plan_zero_daemon_architecture, initial_plan_gated_reflection_dedup, initial_plan_streaming_tag_dispatcher, initial_plan_context_budget, initial_plan_group_intelligence [EXTRACTED 1.00]
- **Image Studio job flow** — docs_agents_and_mcp_imagejobservice, docs_agents_and_mcp_agy_openai_bridge, docs_agents_and_mcp_nox_studio_agent, docs_agents_and_mcp_nox_image_tools, docs_agents_and_mcp_bubblewrap_jail [EXTRACTED 1.00]
- **Sticker pack analysis pipeline** — docs_agents_and_mcp_mediajobservice, docs_agents_and_mcp_sticker_analyst, docs_agents_and_mcp_map_pass, docs_agents_and_mcp_reduce_pass, docs_agents_and_mcp_async_callback_invocation [EXTRACTED 1.00]
- **Shared per-chat workspace quota** — docs_chat_workspace_chatfilestore, docs_chat_workspace_chatimagestore, docs_chat_workspace_chatdocumentstore, docs_chat_workspace_capacity_error [EXTRACTED 1.00]
- **Doc Studio job flow (NoxChat -> bridge -> MCP -> result)** — docs_doc_studio_contracts_docjobservice, docs_doc_studio_contracts_bridge_http_api, docs_doc_studio_contracts_agy_openai_bridge, docs_doc_studio_contracts_nox_doc_tools_mcp, docs_doc_studio_contracts_submit_document, docs_doc_studio_contracts_result_json [INFERRED 0.85]
- **Sticker pack analysis pipeline** — docs_media_and_stickers_analyze_sticker_pack, docs_media_and_stickers_mediajobservice, docs_media_and_stickers_stickeranalyst, docs_media_and_stickers_mediarepository, docs_media_and_stickers_map_reduce_sticker_analysis [EXTRACTED 1.00]
- **Per-turn tip retrieval & injection** — docs_tips_prompt_builder_load_tips, docs_tips_tipservice, docs_tips_nox_tips_collection, docs_tips_sessiondeltatracker [EXTRACTED 1.00]
- **Doc Studio build-render-review-submit loop** — services_nox_doc_tools_agent_tools_run_python, services_nox_doc_tools_agent_tools_render_pptx, services_nox_doc_tools_agent_tools_render_pdf, services_nox_doc_tools_agent_tools_lint_document, services_nox_doc_tools_agent_tools_submit_document [EXTRACTED 1.00]
- **Doc Studio prompt variants (template, with AI, no AI)** — services_agy_openai_bridge_bridge_doc_studio_prompt, services_agy_openai_bridge_workspace_doc_studio__agents_agents_nox_doc_studio, services_agy_openai_bridge_workspace_doc_studio__agents_agents_nox_doc_studio_noai [INFERRED 0.95]
- **Nox sandbox guest image components** — services_nox_sandbox_image_readme_build_sh, services_nox_sandbox_image_readme_nox_init, services_nox_sandbox_image_readme_agent_py, services_nox_sandbox_image_readme_rootfs_ext4, services_nox_sandbox_image_fetch_kernel [EXTRACTED 1.00]

## Communities (217 total, 31 thin omitted)

### Community 0 - "Memory Embeddings & RAG"
Cohesion: 0.05
Nodes (35): main(), reindex_memories(), _delete_memory(), _upsert_memory(), MemoryRecord, MemoryRepository, EmbeddingRunner, count_tokens() (+27 more)

### Community 1 - "Chat Image Store & Inputs"
Cohesion: 0.04
Nodes (45): _sweep_chat_images(), _clips_enabled(), _document_name(), _download(), _image_document_ext(), ImageInputError, message_clip_kind(), message_still_bytes() (+37 more)

### Community 2 - "Chat File Store"
Cohesion: 0.06
Nodes (41): ChatFile, ChatFileStore, ext_of(), FileStoreError, handle_for(), _hash_copy(), kind_for_name(), normalize_ref() (+33 more)

### Community 3 - "DB Repository & FTS"
Cohesion: 0.06
Nodes (41): main(), ChatSearchHit, ChatSearchRepository, fts_pool(), MessageRecord, MessageRepository, build_messages_fts_triggers_sql(), _fts_body_expr() (+33 more)

### Community 4 - "Doc Tools MCP Server"
Cohesion: 0.07
Nodes (63): _artifacts_dir(), _atomic_write(), _bad_workspace_dir(), _bind_path(), _budget_check(), _bwrap(), _check_refs(), chromium_bin() (+55 more)

### Community 5 - "Prompt Tips Service"
Cohesion: 0.06
Nodes (26): TipHit, TipRecord, _now(), text_hash(), TipRepository, set_service(), TipService, TipVectorStore (+18 more)

### Community 6 - "Video Encoding Jail"
Cohesion: 0.05
Nodes (52): available(), build_encode_argv(), bwrap_bin(), _cfg(), encode(), extract_frame(), ffmpeg_bin(), ffprobe_bin() (+44 more)

### Community 7 - "Media Repository & Usage"
Cohesion: 0.07
Nodes (30): _force_add_sticker(), _mutate_media_item(), HandleMap, MediaCaptionRecord, MediaRepository, StickerPackRecord, resync_library_vectors(), _ensure_sticker_captioned() (+22 more)

### Community 8 - "Doc VM Exec Transport"
Cohesion: 0.05
Nodes (42): _delete_mirror(), _file_payload(), _load_sync(), _parent(), _relative(), _rewrite(), rewrite(), rewrite_argument() (+34 more)

### Community 9 - "Sticker Analyst Map-Reduce"
Cohesion: 0.05
Nodes (29): StickerAnalyst, worker(), StickerInput, add_badge(), create_animated_sticker_grid(), create_contact_sheet(), create_static_contact_grid(), image_to_base64_data_uri() (+21 more)

### Community 10 - "Bridge FastAPI App"
Cohesion: 0.05
Nodes (39): create_app(), _busy(), chat_completions(), sse_generator(), _consume(), _make_text_chunk(), doc_studio_create(), doc_studio_delete() (+31 more)

### Community 11 - "Telegram Media Frames"
Cohesion: 0.06
Nodes (39): composite_on_background(), compute_frame_difference(), extract_sticker_frames(), extract_tgs_frames(), extract_webm_frames(), _run_ffmpeg(), filter_motionless_frames(), sample_pil_animated_frames() (+31 more)

### Community 12 - "noxdoc Deck Builder"
Cohesion: 0.09
Nodes (5): en_deck(), fa_deck(), Deck, _emu(), _slide_number_box()

### Community 13 - "Bridge Doc Studio Service"
Cohesion: 0.07
Nodes (21): _ai_image_event(), _AiImageSync, _atomic_write(), _brain_images(), build_create_prompt(), build_message_prompt(), build_steer_prompt(), _check_job_dir() (+13 more)

### Community 14 - "Streaming Tag Parser"
Cohesion: 0.07
Nodes (40): StreamingTagParser, TagEvent, test_streaming_tag_parser_caption_tag(), _CmdMsg, ddb(), _deck(), _docs(), FakeBot (+32 more)

### Community 15 - "ACP Session Pool"
Cohesion: 0.06
Nodes (16): AcpSessionHandle, AcpSessionPool, _cleanup(), _emit(), _text_events(), _is_busy_cancelling_error(), _normalize_model_key(), AgyStreamEvent (+8 more)

### Community 16 - "Doc Job Inputs"
Cohesion: 0.07
Nodes (35): strip_member_prefix(), _autosave_file(), _check_zip(), discard_staged_inputs(), DocInputError, document_filename_from_text(), download_telegram_file(), ext_of() (+27 more)

### Community 17 - "Doc Studio Quotas & Tests"
Cohesion: 0.07
Nodes (51): ai_image_allowance(), _progress_line(), StorageFullError, doc_db(), doc_service(), ev_error(), ev_question(), fn() (+43 more)

### Community 18 - "Dispatcher & Bubble Coalescer"
Cohesion: 0.06
Nodes (24): BufferedBubble, SentBubbleInfo, ToolStatusCoalescer, ActionDispatcher, ActionFailure, ChatWriteForbiddenError, is_chat_write_forbidden(), make_text_action_failure() (+16 more)

### Community 19 - "Prompt Builder & Handles"
Cohesion: 0.06
Nodes (25): compact_media_caption(), format_message_timestamp(), get_same_name_user_ids(), _min_score(), PromptBuilder, _trace_media_rag(), _sha256(), StagedTurnUpdate (+17 more)

### Community 20 - "Sandbox VM Agent"
Cohesion: 0.07
Nodes (13): bind_listener(), force_poweroff(), log(), main(), preferred_python(), PythonSession, read_cmdline_role(), recv_exact() (+5 more)

### Community 21 - "Doc Store & DB Session"
Cohesion: 0.06
Nodes (18): _sweep_chat_files(), get_db(), build_source_bundle(), ChatDocumentStore, _kept(), _ts(), DocRecord, handle_for() (+10 more)

### Community 22 - "Preprocessor Cron Tests"
Cohesion: 0.09
Nodes (57): CronOrigin, preprocessor_tool(), auto_svc(), chain(), code(), cron(), crons(), event() (+49 more)

### Community 23 - "Session Tracker & Token Budget"
Cohesion: 0.06
Nodes (28): _carry_stats(), ChatSessionState, ChunkedHistoryState, _ema(), optimal_growth(), SessionDeltaTracker, test_session_tracker_active_jobs_diff(), turn() (+20 more)

### Community 24 - "User Profile Service"
Cohesion: 0.08
Nodes (15): UserProfileRecord, UserProfileRepository, UserProfileService, UserProfileStore, setup_test_data(), test_private_dm_profiles_strict_isolation(), test_user_profiles_in_prompt_builder(), setup_test_db() (+7 more)

### Community 25 - "Control RPC Handlers"
Cohesion: 0.09
Nodes (54): _add_tips(), _decode_pil_or_tgs_frames_sync(), _delete_hot_memory(), _delete_tips(), _elapsed_ms(), _extract_video_frames_jpeg(), _get_active_jobs(), _get_chat_file_bytes() (+46 more)

### Community 26 - "Agent Tool Registration"
Cohesion: 0.07
Nodes (33): register_audio_tools(), register_cron_tools(), register_history_tools(), SearchChatHistoryInput, register_pdf_tools(), register_workspace_tools(), save_handler(), ws_handler() (+25 more)

### Community 27 - "Response Scheduler"
Cohesion: 0.08
Nodes (34): ChatState, ResponseCoordinator, Timing, wait_event(), _events(), _fake_studio_transport(), _FakeRuntime, image_db() (+26 more)

### Community 28 - "Chat Member Resolution"
Cohesion: 0.07
Nodes (30): normalize_search_text(), _ambiguous(), AmbiguousMemberError, ChatMember, known_members(), MemberLookupError, _norm(), resolve_chat_member() (+22 more)

### Community 29 - "Image Job Service"
Cohesion: 0.07
Nodes (22): BoardEntry, clip_vision_data_url(), CreateImageInput, ImageJobService, _mark_delivered(), create_handler(), jobs_handler(), profile_handler() (+14 more)

### Community 30 - "Hybrid Retrieval Ranker"
Cohesion: 0.09
Nodes (23): _doc_text(), _library_docs(), search_media(), coerce_mode(), rank_documents(), hit(), RankedHit, _as_corpus() (+15 more)

### Community 31 - "Bridge Image Studio"
Cohesion: 0.09
Nodes (30): studio_job(), _collect_results(), delete_session_files(), _inside_output(), _meta(), normalize_output(), normalize_video(), prepare_studio_gemini_dir() (+22 more)

### Community 32 - "noxdoc Inspect Tasks"
Cohesion: 0.07
Nodes (37): slide_flags(), palette(), _box(), check_zip(), _color(), dump_docx(), dump_pptx(), image_info() (+29 more)

### Community 33 - "Tips/Media/Log Search Docs"
Cohesion: 0.05
Nodes (44): PDF edit mode (base_document), Retained-log search implementation, API Calls tab content search, Literal substring log matching, SearchLogs RPC (NoxChatControlService), Signed stateless pagination cursor, Studio Bot Logs tab search UI, LogChunkQuery.summary_only header discovery (+36 more)

### Community 34 - "noxdoc PDF Edit"
Cohesion: 0.12
Nodes (30): _assemble(), _box(), _count(), _fit_page(), html_pages(), images_to_pdf(), _name(), _out() (+22 more)

### Community 35 - "Doc Studio Bridge Tests"
Cohesion: 0.17
Nodes (41): DocJobRequest, DocMessageRequest, _builder(), _client(), _collect(), _create_body(), _job(), _log() (+33 more)

### Community 36 - "Doc Tools Tests"
Cohesion: 0.10
Nodes (32): _assert_result_json(), _grid_image(), Mcp, mcp(), server_scratch(), test_artifacts_dir_is_mounted_read_only(), test_ask_nox_question_and_awaiting(), test_budget_calls_and_turn_reset() (+24 more)

### Community 37 - "noxdoc Deck Edit"
Cohesion: 0.09
Nodes (26): add_slide(), _copy_chart_part(), delete_slide(), duplicate_slide(), EditError, find_shape(), get_layout(), _inherits_center() (+18 more)

### Community 38 - "Chat Quota Service"
Cohesion: 0.09
Nodes (15): ChatRecord, ChatRepository, QuotaService, cmd_changequota(), cmd_usage(), test_cmd_changequota_admin_reset(), test_cmd_changequota_admin_success(), test_cmd_changequota_non_admin_silent() (+7 more)

### Community 39 - "Agent Turn Dispatch"
Cohesion: 0.09
Nodes (25): AgentStepResult, ToolCallItem, on_content_chunk(), on_synth_chunk(), is_chat_left(), reset_write_forbidden_state(), SampleSearchArgs, test_dispatch_agent_turn_forwards_agy_session_metadata_per_step() (+17 more)

### Community 40 - "Doc Studio Contracts"
Cohesion: 0.06
Nodes (42): Document Studio interface contracts, agy-openai-bridge (services/agy-openai-bridge), ask_nox MCP tool, Bridge HTTP API (/v1/doc-studio/jobs), Chat workspace manifest (workspace.json), ChatDocumentStore (src/media/doc_store.py), DocJobService (src/media/doc_jobs.py), DocRecord (+34 more)

### Community 41 - "Chat Inspection & Trace"
Cohesion: 0.10
Nodes (30): _get_chat_history(), _list_chats(), emit_chat_trace(), apply_session_metadata(), current_trace(), emit_trace(), observe_task(), completed() (+22 more)

### Community 42 - "noxdoc Fonts & Lint"
Cohesion: 0.08
Nodes (24): _cmap(), fa_digits(), fit_text(), has_rtl(), _length(), line_height_ratio(), load_font(), measure_block() (+16 more)

### Community 43 - "Workspace Manifest"
Cohesion: 0.07
Nodes (13): _doc_item(), _file_item(), _fmt_bytes(), _image_item(), _kind_matches(), _now(), SaveFileInput, _short() (+5 more)

### Community 44 - "Persona Registry"
Cohesion: 0.09
Nodes (18): PersonaDefinition, PersonaRegistry, _read_ref_file(), _resolve_references(), test_every_persona_inherits_the_rule(), setup_personas(), test_answer_guest_query_http_fallback(), test_answer_guest_query_native() (+10 more)

### Community 45 - "Spike Driver & Worker Pool"
Cohesion: 0.11
Nodes (23): handle(), LoggingPool, rl(), make_pool(), p(), t_ai(), t_cancel_mid(), t_cancel_pool() (+15 more)

### Community 46 - "App Config Models"
Cohesion: 0.09
Nodes (35): AgentConfig, AppConfig, ChatSearchConfig, ContextBudgetConfig, ControlCenterConfig, DocGenConfig, DocSandboxConfig, DocStorageConfig (+27 more)

### Community 47 - "Telegram Runtime"
Cohesion: 0.12
Nodes (17): drain_writes(), cmd_debounce(), handle_write_forbidden(), leave_group_chat(), TelegramRuntime, timing_for(), PendingResponse, isolated() (+9 more)

### Community 48 - "Cron Service"
Cohesion: 0.10
Nodes (17): db(), CronError, Cron, _cron(), _cron_fires(), CronService, _next_fire(), _schedule() (+9 more)

### Community 49 - "noxdoc OOXML Charts"
Cohesion: 0.11
Nodes (20): add_chart(), _crosses(), _font_txpr(), _no_fill_sppr(), _norm_series(), _orientation(), _place_txpr(), _cell_borders() (+12 more)

### Community 50 - "Config: Embeddings & Memory"
Cohesion: 0.05
Nodes (40): embeddings, batch_size, model_name, hot_memory, max_entries, max_entry_tokens, max_total_tokens, ttl_hours (+32 more)

### Community 51 - "Commands & Nicknames"
Cohesion: 0.11
Nodes (24): effective_nicknames(), parse_csv(), is_immune(), is_admin(), cmd_clear(), cmd_crons(), cmd_documents(), cmd_forget() (+16 more)

### Community 52 - "Memory Explorer CLI"
Cohesion: 0.11
Nodes (21): cmd_chats(), cmd_get(), cmd_list(), cmd_rag(), cmd_search(), cmd_stats(), get_chat_map(), get_readonly_connection() (+13 more)

### Community 53 - "Sandbox HTTP Client"
Cohesion: 0.12
Nodes (7): _id(), SandboxClient, test_error_mapping(), test_exec_roundtrip(), test_jobs(), test_management_endpoints_paths(), test_unavailable_when_socket_missing()

### Community 54 - "Control Bridge Server"
Cohesion: 0.07
Nodes (7): _chat_title(), ControlBridgeServer, ControlLogHandler, ControlState, sample_python_runtime_metrics(), remember_call_id(), get_db_path()

### Community 55 - "Persona Guides & Audio Tools"
Cohesion: 0.08
Nodes (14): TranscribeAudioInput, ViewPdfPageInput, ViewPdfPagesInput, handle_search_memories(), SearchMemoriesInput, guide_reminder(), guide_topics(), load_guides() (+6 more)

### Community 56 - "noxdoc Reports & Themes"
Cohesion: 0.12
Nodes (21): en_report(), fa_report(), callout(), chart_svg(), _css(), _digits(), esc(), figure() (+13 more)

### Community 57 - "Doc Job Service Core"
Cohesion: 0.16
Nodes (5): _age_seconds(), DocJobService, _delivered(), _now_iso(), _parse_ts()

### Community 58 - "Audio Benchmark Script"
Cohesion: 0.14
Nodes (14): benchmark(), classify(), SoundClassifier, window_offsets(), read_samples(), write_window(), identify_songs(), parse_song() (+6 more)

### Community 59 - "Config: Audio"
Cohesion: 0.05
Nodes (37): audio, auto_transcribe_max_seconds, beam_size, cache_dir, chunk_seconds, classifier_enabled, classifier_model, classifier_threads (+29 more)

### Community 60 - "Image Tools Workspace Tests"
Cohesion: 0.12
Nodes (21): log(), main(), reply(), _bwrap_works(), _call(), _setup(), test_pillow_script_reads_workspace_read_only(), test_workspace_clip_readable_by_jailed_ffmpeg_outputs_in_job() (+13 more)

### Community 61 - "Audio Job Repository"
Cohesion: 0.12
Nodes (7): AudioRepository, ActiveJob, AudioService, _extract_media_meta(), test_audio_service_song_first_and_duration_limits(), test_durable_progress_recovery_and_cached_reply(), test_oversized_job_finishes_without_download()

### Community 62 - "Preprocessor Automation RPC"
Cohesion: 0.15
Nodes (22): _automation_chat_id(), _automation_chat_titles(), automation_cron_action(), automation_get_preprocessor(), automation_list_crons(), automation_list_preprocessors(), _automation_preprocessor(), automation_preprocessor_action() (+14 more)

### Community 63 - "agy Warm Worker Pool"
Cohesion: 0.08
Nodes (3): WarmAgyWorker, WarmWorkerPool, test_warm_worker_reuse_idle_resume_and_409_session_expired()

### Community 64 - "Sandbox Tools & Disk Index"
Cohesion: 0.12
Nodes (26): list_requests(), cache_manifest(), disk_index_block(), compact_result(), error_result(), _LimitedBuffer, ManageInput, _poll_job() (+18 more)

### Community 65 - "Guest Mode Bot"
Cohesion: 0.08
Nodes (11): main(), guest_update_middleware(), answer_guest_query(), handle_guest_update(), handle_native_guest_message(), parse_persona_prefix(), process_guest_message(), close_runtime() (+3 more)

### Community 66 - "Preprocessor Engine"
Cohesion: 0.14
Nodes (28): _active(), apply_deferred(), _blocked_note(), ChainResult, _create_cron(), _deliver_invoke(), _deliver_say(), _Delivery (+20 more)

### Community 67 - "Video Studio Tool Tests"
Cohesion: 0.19
Nodes (22): _call(), _err(), _frame(), _job(), _mk_clip(), _ok(), _probe(), _recipe_block() (+14 more)

### Community 68 - "Sandbox Agent Tests"
Cohesion: 0.13
Nodes (23): agent(), connect(), Conn, _free_port(), _init(), test_concurrent_sessions_and_busy(), test_exception_reported_session_kept(), test_get_files_caps() (+15 more)

### Community 69 - "Sandbox Policy"
Cohesion: 0.15
Nodes (19): config_allowed(), refresh_grants(), sandbox_allowed(), set_granted(), cmd_sandbox(), env(), FakeClient, _msg() (+11 more)

### Community 70 - "Config: Media"
Cohesion: 0.06
Nodes (32): media, agent_captions, allow_nsfw, background, candidate_oversample, caption_max_chars, cross_chat_library, min_frame_interval_s (+24 more)

### Community 71 - "SearXNG Client"
Cohesion: 0.12
Nodes (9): SearXNGClient, test_searxng_client_all_fail(), test_searxng_client_failover_on_429(), test_searxng_client_failover_on_html_non_json(), test_searxng_client_failover_on_timeout(), mock_get(), test_searxng_client_health_check(), test_searxng_client_parse() (+1 more)

### Community 72 - "Chat Membership Events"
Cohesion: 0.12
Nodes (18): format_sender_identifier(), mark_chat_rejoined(), handle_message(), handle_my_chat_member(), get_runtime(), dispatched_image_urls(), handle_and_wait(), test_add_watermark_and_grid() (+10 more)

### Community 73 - "Agents & MCP Docs"
Cohesion: 0.07
Nodes (29): agy-openai-bridge, ask_nox / note_to_nox, Async Callback Invocation, Built-in Agent Tools (23 native tools), Agents & MCP Tools, DocJobService (src/media/doc_jobs.py), Document Studio, Image Studio (+21 more)

### Community 74 - "LLM Client"
Cohesion: 0.13
Nodes (11): emit_api_call(), _capture_failure(), _extract_images_from_messages(), is_non_retriable_error(), is_session_expired_error(), LLMClient, test_completion_caller_is_telemetry_only(), test_llm_client_retry_on_connect_timeout() (+3 more)

### Community 75 - "Image Tools MCP Server"
Cohesion: 0.11
Nodes (19): _budget_check(), _budget_check_locked(), _count_key(), _dispatch(), _drain(), _draw_text(), _font_path(), handle() (+11 more)

### Community 76 - "Config: Doc Gen"
Cohesion: 0.07
Nodes (30): doc_gen, copy_to_sandbox, enabled, executor, extend_report_before_seconds, extend_step_seconds, input_max_bytes, job_timeout_seconds (+22 more)

### Community 77 - "Doc Job Lifecycle"
Cohesion: 0.11
Nodes (6): create_handler(), job_handler(), _job_executor(), normalize_job_ref(), _short(), _studio_url()

### Community 78 - "PDF Render & Vision"
Cohesion: 0.17
Nodes (20): PdfConfig, is_pdf_document(), choose_layout(), data_url(), Layout, page_count(), page_sizes(), render_collage() (+12 more)

### Community 79 - "Control Direct Chat"
Cohesion: 0.15
Nodes (11): _file_ref(), MockDocument, MockMessage, MockUser, next_message_id(), process_direct_message(), hooked_create(), wrapped_proc() (+3 more)

### Community 80 - "Qdrant Client Pool"
Cohesion: 0.12
Nodes (8): QdrantClientPool, setup_test_env(), setup_test_db(), setup_test_db(), db(), db(), test_embedded_qdrant_store_different_collections(), test_qdrant_client_pool_reuses_client()

### Community 81 - "Preprocessor Media Handles"
Cohesion: 0.13
Nodes (11): MediaHandle, media_refs(), resolve_media(), StableMediaMap, _hmap(), _library(), test_cron_say_pins_handles(), test_dispatcher_bare_reply_and_stable_sticker() (+3 more)

### Community 82 - "Videokit FFmpeg"
Cohesion: 0.11
Nodes (14): t_run_ffmpeg(), map_output(), atempo_chain(), check_ffmpeg_args(), check_final(), enable_expr(), _f(), FfmpegArgError (+6 more)

### Community 84 - "Config: Stickers"
Cohesion: 0.08
Nodes (25): stickers, analyst_context_messages, analyst_model, animated_cell_size, animated_max_frames, animated_per_call, cell_size, diagnostics (+17 more)

### Community 85 - "Search Endpoints"
Cohesion: 0.15
Nodes (7): load_searxng_endpoints(), parse_endpoints_data(), resolve_endpoint_file_path(), __getattr__(), SanitizedContent, SearchResult, test_endpoints_loader()

### Community 86 - "Message References"
Cohesion: 0.16
Nodes (12): extract_forward_origin(), _fetch_url_content(), _sync_fetch(), parse_telegram_links(), resolve_all_references(), resolve_telegram_reference(), ResolvedReference, TelegramLink (+4 more)

### Community 87 - "Config: Search"
Cohesion: 0.09
Nodes (23): search, aggregation_budget_chars, aggregation_max_tokens, aggregation_model, allowed_categories, blocked_domains, default_categories, default_language (+15 more)

### Community 88 - "Image Tools Clip Ops"
Cohesion: 0.24
Nodes (20): _clip(), _clip_result(), _enable(), _ffmpeg_to(), _inp(), _opt_float(), _out_args(), _overlay_clip() (+12 more)

### Community 89 - "Image Tools Job Context"
Cohesion: 0.26
Nodes (19): _color(), JobContext, _open(), _save(), t_add_text(), t_adjust(), t_collage(), t_convert() (+11 more)

### Community 90 - "Audio Preparation"
Cohesion: 0.23
Nodes (15): AudioProcessingError, metrics(), prepare_audio(), run_process(), AudioConfig, database(), test_classifier_multilabel_and_uncertain(), test_conversion() (+7 more)

### Community 91 - "Search Aggregator"
Cohesion: 0.16
Nodes (9): _is_boilerplate(), _jaccard_similarity(), SearchAggregator, _word_trigrams(), AggregatedResult, FetchedPage, SearchHit, test_extractive_dedup_and_budget() (+1 more)

### Community 92 - "Content Sanitizer"
Cohesion: 0.15
Nodes (7): ContentSanitizer, test_injection_pattern_detection(), test_invisible_char_stripping(), test_persian_zwnj_preserved(), test_tag_neutralization(), test_url_validation_allows_valid(), test_url_validation_blocks_ssrf()

### Community 94 - "Cron & Preprocess Config"
Cohesion: 0.09
Nodes (8): CronConfig, PreprocessConfig, auto(), Clock, FakeBot, FakeDispatcher, FakePre, FakeRuntime

### Community 95 - "Media Vector Store"
Cohesion: 0.19
Nodes (3): MediaVectorStore, test_media_vector_store(), setup_test_env()

### Community 96 - "Image Tools Atomic IO"
Cohesion: 0.16
Nodes (10): _animated_webp(), _atomic_write(), _bad_workspace_dir(), clip_source(), _create_new(), _overlaps(), _safe_copy(), map_input() (+2 more)

### Community 97 - "Image Tools bwrap Jail"
Cohesion: 0.13
Nodes (15): _bin_binds(), _bwrap(), _ffmpeg_bin(), _final_clip(), jail_base(), probe(), _python_binds(), _regular() (+7 more)

### Community 98 - "Local Tool Registry"
Cohesion: 0.12
Nodes (6): compact_schema(), LocalTool, test_tool_image_result(), test_hidden_alias_still_executes(), test_registered_schema_is_compact_and_alias_is_hidden_but_callable(), handler()

### Community 99 - "Image Studio Deployment Docs"
Cohesion: 0.12
Nodes (19): Bubblewrap jail (fail closed), build_encode_argv shared encode recipe, nox-image-tools Pillow MCP server, nox-studio agent, videokit clip tools, tp1 host, control_center: section, agy-openai-bridge.service (+11 more)

### Community 100 - "Sandbox Client Test Server"
Cohesion: 0.13
Nodes (9): SandboxConfig, test_source_code_formats_are_exportable(), fake_sandbox(), FakeSandbox, test_config_defaults_match_contract(), test_config_global_alias_from_yaml(), test_policy(), test_policy_runtime_grants() (+1 more)

### Community 101 - "Message Deletion Tools"
Cohesion: 0.19
Nodes (11): delete_message_handler(), DeleteMessageInput, _get_bot(), leave_group_handler(), LeaveGroupInput, _preview(), _FakeBot, test_leave_group_handles_already_kicked_and_transient_errors() (+3 more)

### Community 102 - "Message Events & Search Shutdown"
Cohesion: 0.13
Nodes (13): build_message_event(), shutdown_search(), find_media_attachment(), _audio_text(), _document_filename(), _document_text(), _has_field(), record_for() (+5 more)

### Community 103 - "Architecture Docs"
Cohesion: 0.16
Nodes (18): ACP & Worker Pooling, ActionDispatcher (dispatcher.py), agy-openai-bridge, NoxChat Architecture, Document Studio Architecture, Dual History Modes (openai / agy), Embedded Qdrant, FastEmbed CPU ONNX (jina-embeddings-v3) (+10 more)

### Community 104 - "noxdoc CLI"
Cohesion: 0.26
Nodes (12): CLIError, _inspect(), _lint(), main(), Parser, _preview(), _render_pdf(), _render_pptx() (+4 more)

### Community 105 - "Doc & Image Quotas"
Cohesion: 0.16
Nodes (13): check_doc_quota(), doc_quota_exempt(), _doc_usage(), charged_engine(), check(), _fmt_wait(), is_exempt(), _limits() (+5 more)

### Community 106 - "Doc Job Sandbox Delivery"
Cohesion: 0.16
Nodes (7): cleanup_doc_job_dir(), _copy_to_sandbox(), doc_jobs_root(), _delivered(), _delivered(), _latest_collage(), _safe_filename()

### Community 107 - "Control Files RPC Tests"
Cohesion: 0.20
Nodes (13): _clip_bytes(), _pdf_bytes(), _png_bytes(), rpc_env(), test_get_chat_file_bytes(), test_get_chat_file_preview_image_and_disk_cache(), test_get_chat_file_preview_pdf_and_doc(), test_get_chat_file_preview_text() (+5 more)

### Community 108 - "Config: Image Gen"
Cohesion: 0.11
Nodes (18): image_gen, enabled, guest_mode_enabled, input_max_bytes, job_timeout_seconds, max_active_jobs_per_chat, max_concurrent_jobs, max_inputs (+10 more)

### Community 109 - "Doc Studio Workflow Docs"
Cohesion: 0.23
Nodes (17): Doc Studio workflow (inspect, outline, build, render, review, fix, submit), AGENT_TOOLS.md (doc_tools and noxdoc reference), ask_nox tool, Broken reference lint, Custom-designed PDF HTML template, doc_tools MCP server, inspect_input tool, lint_document tool (+9 more)

### Community 110 - "PDF Edit Unreadable Handling"
Cohesion: 0.13
Nodes (12): PdfUnreadable, _edit_job(), _image_pdf(), _page_texts(), _sizes(), test_html_pages_render_matches_base_and_inserts(), test_pdf_edit_mode_merge_preview_and_submit(), test_pdfedit_encrypted_and_damaged() (+4 more)

### Community 111 - "Preprocessor Limits"
Cohesion: 0.22
Nodes (15): fires_today(), increment_say_streak(), _key(), load_streaks(), log_count(), note_llm_call(), parse_time(), _persist_reset() (+7 more)

### Community 112 - "PDF Documents Store"
Cohesion: 0.20
Nodes (4): LimitedPdfFile, PdfDocuments, PdfError, test_scoped_persistent_references()

### Community 113 - "Studio SSE Client"
Cohesion: 0.18
Nodes (6): auth_headers(), bridge_v1_base(), sse_events(), studio_delete(), studio_post(), StudioHTTPError

### Community 114 - "Forced STT Differentiation"
Cohesion: 0.17
Nodes (8): _request_forces_stt(), transcribe_audio_handler(), AudioAnalysis, test_explicit_transcribe_phrase_detection(), test_record_for_exposes_audio_and_document_filenames(), test_transcribe_audio_tool_rejects_over_20_min_and_queues_under(), test_description_does_not_conflate_error_and_silence(), test_timestamped_sound_events_and_segments_in_describe()

### Community 115 - "Sandbox Library Requests"
Cohesion: 0.20
Nodes (10): approve_requests(), check_installed(), decide_requests(), now(), parse_package(), reject_requests(), request_libraries(), start_watcher() (+2 more)

### Community 117 - "Commands & README"
Cohesion: 0.21
Nodes (15): NoxChat Telegram Slash Commands, Admin-only commands (changequota, sweepimages, sweepdocs), BotFather /setcommands list, command_router (18 handlers, 20 invocations), Dual Mode DM & Group Intelligence, NoxChat README, Unified Chat Workspace, Document Studio (+7 more)

### Community 118 - "Doc Studio Prompt Template"
Cohesion: 0.14
Nodes (12): doc_studio_prompt.md (Doc Studio system prompt template), ask_nox (non-blocking question to Nox), {{ASSET_POLICY}} placeholder, Turn time/tool-call budget and STEP BACK rule, Chat workspace (read-only IMG/DOC/FILE library), Design rules (16:9 decks, 12-col grid, contrast, fonts, RTL), Job folder (inputs/work/output), Job spec (kind/mode/brief/limits/budget) (+4 more)

### Community 119 - "Speech-to-Text"
Cohesion: 0.22
Nodes (6): merge_transcripts(), SpeechToText, TranscriptionResult, TranscriptSegment, test_stt_duration_gated_language_detection(), test_stt_word_boundaries_and_multilingual()

### Community 120 - "Segment Search & RRF Plan"
Cohesion: 0.16
Nodes (13): /clear (/reset) archive command, Reciprocal Rank Fusion (Qdrant + FTS5), Qdrant chat_segments collection, ChatSearchRepository.search, EmbeddingRunner (embed_passages / embed_query), EmbedGate (src/memory/embed_gate.py), msg_segments / msg_segment_members / segment_cursor tables, rank_documents tiered ranker (+5 more)

### Community 121 - "Chat Workspace Docs"
Cohesion: 0.24
Nodes (13): Job-input auto-save, Bridge workspace request field, Workspace.capacity_error shared quota, ChatDocumentStore, ChatFileStore / chat_files table, ChatImageStore, doc-studio-contracts additions, Chat Workspace design (+5 more)

### Community 122 - "Info Score & Context Budget"
Cohesion: 0.15
Nodes (12): Message Info Score & Segment Embedding Plan, chat_search.info_score / semantic config block, Contextual Keep Decision, explain_message_info debug RPC, Filtered Segment Embedding, info_score (message information score), jina-v3 embedding model, multilingual-MiniLM-L12 fallback model (+4 more)

### Community 123 - "Config: Telegram"
Cohesion: 0.15
Nodes (13): telegram, allow_voluntary_leave, auto_leave_restricted_strikes, auto_reply_interval, bot_token, bot_username, cooldown_seconds, default_trigger_name (+5 more)

### Community 124 - "Image Studio Agent"
Cohesion: 0.23
Nodes (10): nox-studio agent definition (Nox Image Studio), add_text (RTL-shaped captions), Clip tools (video_info, video_frames, video_text, ...), image_tools MCP server (Pillow tools), make_sticker (Telegram WebP), ENGINE: PILLOW-ONLY mode, run_ffmpeg (last resort), run_pillow_script (+2 more)

### Community 125 - "Sandbox Errors"
Cohesion: 0.19
Nodes (4): SandboxError, SandboxUnavailable, _FakeSandbox, down()

### Community 126 - "Search Tool Singletons"
Cohesion: 0.24
Nodes (8): get_aggregator(), get_page_fetcher(), get_sanitizer(), get_searx_client(), handle_web_read(), handle_web_search(), WebReadInput, WebSearchInput

### Community 127 - "Response Triggers"
Cohesion: 0.33
Nodes (9): should_respond(), make_mock_message(), test_config_bot_username_mention(), test_dm_always_responds(), test_group_mention_triggers(), test_group_nickname_triggers(), test_group_reply_to_bot_triggers(), test_group_without_trigger_ignored() (+1 more)

### Community 128 - "Config: Context Budget"
Cohesion: 0.17
Nodes (12): context_budget, cached_input_cost_ratio, history_chunk_size, max_context_tokens, max_memory_tokens, max_per_memory_tokens, min_memory_score, rebase_growth_max_tokens (+4 more)

### Community 129 - "Config: User Limits"
Cohesion: 0.20
Nodes (12): user_limits, user_limits, per_5_hours, per_day, per_hour, day, enabled, exempt_admins (+4 more)

### Community 130 - "Config: Tips"
Cohesion: 0.17
Nodes (12): tips, collection, enabled, max_tip_chars, max_tips, max_tips_per_chat, max_trigger_chars, min_relevance (+4 more)

### Community 131 - "noxdoc Icons"
Cohesion: 0.26
Nodes (6): _bundle(), icon_names(), icon_png(), icon_svg(), icons_dir(), search_icons()

### Community 132 - "Initial Master Plan"
Cohesion: 0.23
Nodes (9): NoxChat Master Architecture & Implementation Plan, 5-Layer Storage Architecture (L0-L4), Phased Implementation Roadmap (Phase 0-5), Project Mastermind (predecessor), Tagging Protocol (thought/react/reply/msg), requirements.txt (core Python dependencies), aiogram, aiosqlite (+1 more)

### Community 133 - "Bridge Workspace Checks"
Cohesion: 0.33
Nodes (5): check_workspace(), _forbidden(), _overlaps(), recheck(), Workspace

### Community 134 - "noxdoc Image Helpers"
Cohesion: 0.21
Nodes (4): _find_cuts(), _line_stats(), split_grid(), _trim()

### Community 135 - "Sandbox Image README"
Cohesion: 0.27
Nodes (11): nox-sandbox-image README, agent.py (in-VM vsock agent), build.sh (rootfs builder), plans/2026-11-20-nox-sandbox/contracts.md, Firecracker microVM, Guest roles (chat, inspector, temp, pre), manifest.json build output, nox-init (PID-1 init) (+3 more)

### Community 136 - "Config: LLM"
Cohesion: 0.18
Nodes (11): llm, api_key, base_url, history-mode, max_output_tokens, max_session_turns, model, provider (+3 more)

### Community 137 - "Audio & Reply Timing Docs"
Cohesion: 0.20
Nodes (11): AST sound classifier, Audio job persistence & chunked resume, /debounce command, 1 GB decoded-file budget, Audio and reply timing, faster-whisper local STT (large-v3), Reply scheduling / debounce, ShazamIO song recognition (+3 more)

### Community 138 - "Doc Studio Settings"
Cohesion: 0.27
Nodes (5): DocStudioSettings, prepare_doc_studio(), render_agent_definitions(), write_agent_definitions(), test_prepare_doc_studio_writes_mcp_config_and_agents()

### Community 139 - "noxdoc SVG Art"
Cohesion: 0.29
Nodes (4): initials(), monogram_svg(), svg_to_png(), test_svg_art_helpers_and_deck_svg()

### Community 140 - "Log Search Plan"
Cohesion: 0.24
Nodes (9): Updated Search Plan: chat FTS and retained log search, Local filter over RecentApiCalls, noxchat.proto (QueryLogChunks, GetApiCallDetail), Probe spool (nox-spool-*.wal), RawLogsApiTabViewModel (Studio), Retained-frame Catalog, Search continuation cursor & snapshot, SearchLogs RPC (+1 more)

### Community 141 - "Deck/PDF Edit Docs"
Cohesion: 0.22
Nodes (10): Edit mode for uploaded decks, Edit mode for PDFs, Deck edit API (open_deck, set_shape_text, replace_text, ...), monogram_svg, noxdoc.Deck, noxdoc library, noxdoc.pdfedit, run_python tool (+2 more)

### Community 142 - "Config: Chat Search"
Cohesion: 0.22
Nodes (9): chat_search, context_messages, enabled, exclude_active_window, include_archived, like_fallback, max_result_chars, max_results (+1 more)

### Community 143 - "Config: Sandbox VM"
Cohesion: 0.22
Nodes (9): mem_mb, pool_size, sandbox, allow_all_chats, chat_vm, chats, enabled, enabled_chats (+1 more)

### Community 144 - "Config: Video"
Cohesion: 0.22
Nodes (9): video, crf, enabled, max_input_seconds, max_output_bytes, max_seconds, max_side, studio_budget_seconds (+1 more)

### Community 145 - "Config: Retrieval"
Cohesion: 0.22
Nodes (9): retrieval, default_mode, fuzzy_min, fuzzy_strong, history_candidates, history_vocab_terms_per_token, history_vocab_ttl_seconds, semantic_candidates (+1 more)

### Community 147 - "Info Scorer Training"
Cohesion: 0.22
Nodes (7): Dual LLM Labelling (Gemini 4 Neon + Sonnet), Standalone Evaluation Stage (eval/chat_search, E0-E9), InfoScorer (src/history/info_score.py), InfoSweeper background rescoring task, Ship Criteria (C.5), SQLite FTS5 official documentation, Antigravity (agy) OpenAI Bridge

### Community 148 - "Persona: Minthara"
Cohesion: 0.22
Nodes (8): aliases, base, identity, key, name, role, tone, voice_guidelines

### Community 149 - "Persona: Nox"
Cohesion: 0.22
Nodes (8): aliases, base, identity, key, name, role, tone, voice_guidelines

### Community 151 - "Config: Control Center"
Cohesion: 0.25
Nodes (8): control_center, enabled, listen_host, listen_port, log_buffer_bytes, max_spool_bytes, state_buffer_bytes, store_raw_logs

### Community 152 - "Config: Rate Limit"
Cohesion: 0.25
Nodes (8): rate_limit, dm_limit_24h, dm_limit_5h, enabled, exceeded_message_24h, exceeded_message_5h, group_limit_24h, group_limit_5h

### Community 153 - "DB Backup Script"
Cohesion: 0.54
Nodes (7): cleanup(), die(), is_active(), log(), restart_stopped(), dbbackup.sh script, usage()

### Community 154 - "PDF Overlay Drawing"
Cohesion: 0.32
Nodes (6): _font_family(), _mpl_overlay(), _overlay_file(), draw(), watermark(), draw()

### Community 155 - "Default Persona YAML"
Cohesion: 0.25
Nodes (7): aliases, identity, name, role, tag_rules, tone, voice_guidelines

### Community 156 - "Config: Agent"
Cohesion: 0.29
Nodes (7): agent, coalesce_max_bubble_chars, max_tool_steps, step_timeout_seconds, tool_status, tool_status_separator, tool_timeout_seconds

### Community 157 - "Config: GIFs"
Cohesion: 0.29
Nodes (7): gifs, enabled, max_caption_tokens, pinned_max, rag_min_score, rag_top_k, session_slots

### Community 158 - "Config: Workspace"
Cohesion: 0.29
Nodes (7): workspace, enabled, manifest_max_items, max_bytes_per_chat, max_file_bytes, max_total_bytes, subdir

### Community 161 - "Report HTML Template"
Cohesion: 0.33
Nodes (7): noxdoc.report (render_report, chart_svg, ...), render_report, base.html.j2 (noxdoc report Jinja template), Cover section (.cover, kicker, title, subtitle, meta), Doc header (cover=False), lang/dir RTL attributes, TOC nav

### Community 162 - "Config: Storage"
Cohesion: 0.53
Nodes (6): storage, storage, keep_job_dirs, max_bytes_per_chat, max_total_bytes, subdir

### Community 163 - "Config: Guest Mode"
Cohesion: 0.33
Nodes (6): guest_mode, allow_persona_prefix, enabled, llm_timeout_seconds, max_response_tokens, record_guest_messages

### Community 164 - "Guide: Workspace"
Cohesion: 0.33
Nodes (5): guides, workspace, text, title, tools

### Community 165 - "Bridge Requirements"
Cohesion: 0.33
Nodes (6): agy-openai-bridge requirements.txt, fastapi>=0.110.0, httpx>=0.27.0, pytest>=8.0.0, pytest-asyncio>=0.23.0, uvicorn[standard]>=0.29.0

### Community 166 - "Doc Studio Agent Variants"
Cohesion: 0.40
Nodes (4): nox-doc-studio agent definition, Agent tools: read_file, generate_image, nox-doc-studio-noai agent definition, split_grid

### Community 167 - "PDF Vision Fixtures"
Cohesion: 0.33
Nodes (3): documents(), make_pdf(), poppler()

### Community 168 - "Config: App"
Cohesion: 0.40
Nodes (5): app, data_dir, env, log_level, name

### Community 169 - "Config: Cron"
Cohesion: 0.40
Nodes (5): cron, default_timezone, enabled, nested_min_delay_seconds, notify_on_create

### Community 170 - "Config: Sandbox Exec"
Cohesion: 0.40
Nodes (5): sandbox, exec_margin_s, keep_jobs, lock_wait_s, sync_max_file_bytes

### Community 171 - "Config: Memory"
Cohesion: 0.40
Nodes (5): memory, cross_chat_memory, dedup_window_days, history_chunk_size, similarity_threshold

### Community 172 - "Config: Profile"
Cohesion: 0.40
Nodes (5): profile, check_interval_hours, enabled, max_profile_entries, min_relevance

### Community 174 - "Guide: Chat History"
Cohesion: 0.40
Nodes (5): remind_on, text, title, tools, chat_history

### Community 175 - "Guide: Cron"
Cohesion: 0.40
Nodes (5): remind_on, text, title, tools, cron

### Community 176 - "Guide: Document Studio"
Cohesion: 0.40
Nodes (5): remind_on, text, title, tools, document_studio

### Community 177 - "Guide: Image Studio"
Cohesion: 0.40
Nodes (5): image_studio, remind_on, text, title, tools

### Community 178 - "Guide: Preprocessors"
Cohesion: 0.40
Nodes (5): preprocessors, remind_on, text, title, tools

### Community 179 - "Guide: Sandbox"
Cohesion: 0.40
Nodes (5): sandbox, remind_on, text, title, tools

### Community 180 - "Guide: Stickers"
Cohesion: 0.40
Nodes (5): stickers, remind_on, text, title, tools

### Community 181 - "Font Availability"
Cohesion: 0.60
Nodes (4): is_installed(), resolved_family(), t_list_fonts(), avail()

### Community 182 - "Sticker Analyst Prompts"
Cohesion: 0.40
Nodes (4): map, system, reduce, system

### Community 184 - "Deck XML Tests"
Cohesion: 0.60
Nodes (5): _chart_xmls(), _check_rpr_order(), _slide_xml(), test_en_deck_ltr_xml(), test_fa_deck_rtl_xml_and_native_chart()

### Community 187 - "Config: AI Limits"
Cohesion: 0.50
Nodes (4): per_5_hours, per_day, per_hour, ai

### Community 188 - "Base Persona Rules"
Cohesion: 0.50
Nodes (3): conversation_rules, tag_rules, tool_rules

### Community 189 - "Guide: Audio"
Cohesion: 0.50
Nodes (4): text, title, tools, audio

### Community 190 - "Guide: Files"
Cohesion: 0.50
Nodes (4): text, title, tools, files

### Community 191 - "Guide: Messages"
Cohesion: 0.50
Nodes (4): messages, text, title, tools

### Community 192 - "Guide: Profiles"
Cohesion: 0.50
Nodes (4): profiles, text, title, tools

### Community 193 - "Guide: Rich Messages"
Cohesion: 0.50
Nodes (4): rich_messages, text, title, tools

### Community 194 - "Guide: Tips"
Cohesion: 0.50
Nodes (4): tips, text, title, tools

### Community 195 - "Guide: Web Search"
Cohesion: 0.50
Nodes (4): web_search, text, title, tools

### Community 196 - "Audio Requirements"
Cohesion: 0.67
Nodes (4): Local Audio & Vision Intelligence, requirements-audio.txt (optional local audio stack), faster-whisper, shazamio

## Knowledge Gaps
- **99 isolated node(s):** `Tagging Protocol (thought/react/reply/msg)`, `InfoSweeper background rescoring task`, `multilingual-MiniLM-L12 fallback model`, `chat_search.info_score / semantic config block`, `explain_message_info debug RPC` (+94 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 1779 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **31 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Audio and reply timing` connect `Audio & Reply Timing Docs` to `Audio Benchmark Script`, `Image Studio Deployment Docs`?**
  _High betweenness centrality (0.131) - this node is a cross-community bridge._
- **Are the 66 inferred relationships involving `MessageRepository` (e.g. with `process_direct_message()` and `_get_chat_history()`) actually correct?**
  _`MessageRepository` has 66 INFERRED edges - model-reasoned connections that need verification._
- **What connects `Tagging Protocol (thought/react/reply/msg)`, `InfoSweeper background rescoring task`, `multilingual-MiniLM-L12 fallback model` to the rest of the system?**
  _99 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Memory Embeddings & RAG` be split into smaller, more focused modules?**
  _Cohesion score 0.04896421845574388 - nodes in this community are weakly interconnected._
- **Why does `audio: section` connect `Audio & Reply Timing Docs` to `Config: Embeddings & Memory`?**
  _High betweenness centrality (0.126) - this node is a cross-community bridge._
- **Should `Chat Image Store & Inputs` be split into smaller, more focused modules?**
  _Cohesion score 0.041932841932841934 - nodes in this community are weakly interconnected._
- **Why does `get_db()` connect `Doc Store & DB Session` to `Memory Embeddings & RAG`, `Chat Image Store & Inputs`, `Chat File Store`, `DB Repository & FTS`, `Prompt Tips Service`, `Video Encoding Jail`, `Media Repository & Usage`, `Sticker Analyst Map-Reduce`, `Telegram Media Frames`, `Streaming Tag Parser`, `Doc Job Inputs`, `Doc Studio Quotas & Tests`, `Dispatcher & Bubble Coalescer`, `Prompt Builder & Handles`, `Preprocessor Cron Tests`, `User Profile Service`, `Control RPC Handlers`, `Agent Tool Registration`, `Response Scheduler`, `Chat Member Resolution`, `Image Job Service`, `Hybrid Retrieval Ranker`, `Chat Quota Service`, `Chat Inspection & Trace`, `Workspace Manifest`, `Persona Registry`, `Telegram Runtime`, `Cron Service`, `Commands & Nicknames`, `Control Bridge Server`, `Doc Job Service Core`, `Audio Job Repository`, `Preprocessor Automation RPC`, `Sandbox Tools & Disk Index`, `Doc Job Lifecycle`, `PDF Render & Vision`, `Control Direct Chat`, `Audio Repository Module`, `Message Deletion Tools`, `Message Events & Search Shutdown`, `Doc & Image Quotas`, `Doc Job Sandbox Delivery`, `Control Files RPC Tests`, `Preprocessor Limits`, `PDF Documents Store`, `Sandbox Library Requests`?**
  _High betweenness centrality (0.060) - this node is a cross-community bridge._