# Progress

## Deploy 1 (TTS)
- [x] Local NoxChat suite with pending changes: 696 passed, 1 skipped (scratch venv, py3.13)
- [x] Review Go tts_proxy + firecracker v_53 binding: OK (role-gated chat/temp, chat_id host-stamped, frame caps, listener closed on Stop, restore uses same start path). `go test -race ./pkg/noxsandbox/...` green.
- [x] Review nox-tts daemon: config.yaml `tts:` was dead config (daemon used env vars, other defaults; max_requests_per_hour unenforced) -> FIXED: services/nox-tts/settings.py reads the `tts:` section from /run/nox-tts/config.yaml (unit ExecStartPre copies ~/NoxChat/config.yaml); requests/hour enforced; clone_enabled honoured; default voices used; exempt_admins removed (daemon can't know admins). PyYAML added to requirements. docs/configuration.md `tts` + `sandbox.tts` sections. Tests added (15 pass).
- [x] Studio-side review (Sonnet subagent), findings:
  - B1 kind=video jobs always fail: doc_jobs `_on_result` expects `.video` suffix (also filename/doc_store/sandbox copy/dispatcher/bridge fallback) -> TODO
  - B2 nox-doc-tools render_deck_video non-VM branch parses untrusted pptx/pdf on host -> TODO
  - S1 video brief/prompt says PDF; S2 submit_document video unverified (no probe/duration); S3 DocRecord video send loses file_id; S5 noxtts client picks vsock on host if /dev/vsock exists; S8/S9 unbounded cues/scenes/pages + hardcoded limits; S4 audio bitrate 64k->96k for all clips (intended, note)
  - S6 partly wrong: sandbox.tts IS read by Go (nox-sandbox reads config.yaml `sandbox:`)
- [x] Fixed (me): B1 doc_ext() kind->extension (video=.mp4) in doc_store/doc_jobs/dispatcher/bridge fallback; B2 render_deck_video refuses non-VM executor; S3 DocRecord video keeps file_id; S5 host servers force the Unix socket. Tests added (test_doc_studio, test_doc_delivery, test_doc_tools, test_tts_integration).
- [x] Fixed (agy Neon delegate 9cedeea7, reviewed): S1 video wording; S2 submit_document video requires an unchanged render_deck_video record + duration cap; S8/S9 new config keys doc_gen.video_vm_timeout_seconds/video_max_slides, image_gen.video.storyboard_max_scenes/video_max_subtitle_cues/video_max_pause_ms; numeric args -> ToolError; segment-count drift -> error. Two assertion updates (tests/test_video.py, bridge tests/test_studio.py) for the new registry keys.
- [x] Found while provisioning tp1 (agy hallucinations), fixed:
  - `piper:fa-mana` does not exist in sherpa-onnx releases (404). Default FA voice -> `piper:fa-amir`; added fa-ganji, fa-reza (no female Persian Piper voice exists). Catalog, aliases, config, docs, guides, tests updated.
  - supertonic URL wrong (real asset: sherpa-onnx-supertonic-3-tts-int8-2026-05-11.tar.bz2)
  - cloner `TTSModel.load_model(str(local_dir))` passed a path as `language`; pocket-tts downloads from HF at runtime (unit is PrivateNetwork). Now: `fetch_models.py --pocket` fills HF_HOME=/var/lib/nox-tts/models/hf; unit sets HF_HUB_OFFLINE=1.
- [x] Adopted tp1-only edits into repo (see machines.md)
- [ ] nox-tts suite, Go noxsandbox tests, bridge suite, full NoxChat suite
- [ ] Commit NoxChat + MADTOM (Go)
- [ ] tp1: pre-flight (git diff config.yaml, active jobs, RAM/disk)
- [x] tp1: provision nox-tts — apt espeak-ng flite python3-venv, user nox-tts (999, group nox-sandbox), /opt/nox-tts/venv (torch 2.15 cpu, pocket-tts 4.0.1, PyYAML), models 949 MB + pocket HF cache 222 MB
- [x] tp1 direct engine test (no daemon, as nox-tts): sherpa-onnx 2.0.3 -> Piper x7 + Kitten x2 SILENT (2.x dropped the espeak frontend). Pinned `sherpa-onnx>=1.13.8,<2` -> all 28 voices OK. RTF: piper ~0.3, supertonic ~0.25, kitten ~0.4, espeak/flite ~0.03, **kokoro ~1.4-1.7 (slower than real time; default EN voice)**
- [ ] tp1 cut-over (needs no active jobs), order:
  1. stash tp1 local edits (adopted in repo), pull NoxChat; suite in /tmp/nox-verify worktree first
  2. host.yaml += tts_socket_path /run/nox-tts/api.sock, tts_vsock_port 53; install new nox-sandbox binary (/usr/local/bin, keep .prev); restart nox-sandbox
  3. install nox-tts code to /opt/nox-tts + unit; start; socket check (perms 0660 root? owner nox-tts:nox-sandbox)
  4. image: install build.sh -> /opt/nox-sandbox/image/, rsync noxdoc -> /opt/nox-sandbox/nox-doc-tools/noxdoc/, **noxtts client -> /opt/nox-sandbox/nox-tts/client/noxtts.py**; POST /v1/image/rebuild; restart hibernated VMs
  5. restart agy-openai-bridge + noxchat
- [ ] tp1: nox-sandbox binary + host.yaml + guest image rebuild + restart hibernated VMs
- [ ] tp1: pull NoxChat, suites in detached worktree, restart bridge + noxchat
- [ ] tp1: TTS engine tests (every engine, fa/en, clone), VM vsock allow (chat) / deny (pre)
- [ ] Log in Devices/tp1/configurations/noxchat-deploys.md

## Deploy 2 (/changequota overrides)
- [ ] Design + schema
- [ ] Implement + tests
- [ ] Deploy + live check

## Log
