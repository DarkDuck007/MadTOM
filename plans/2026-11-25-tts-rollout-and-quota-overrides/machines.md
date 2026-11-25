# Changes per machine / environment

## dev (this box)
- Scratch venv for NoxChat tests: session scratchpad `noxvenv` (py3.13, requirements.txt + pytest).
- Adopted tp1-only edits into the repo (repo = source of truth):
  - `config.yaml` `image_gen.studio_model: "Gemini 4 Hydrogen (Low)"` (tp1 edit 2026-11-25 02:53)
  - `services/agy-openai-bridge/workspace-doc-studio/.agents/agents/nox-doc-studio{,-noai}.md`
    (contact-sheet / view_pages review workflow, tp1 edit 2026-11-24 14:33; matches commit 8e51225)

## tp1 — pre-flight (2026-11-25, read-only)
- user danial uid 1000, in group `nox-sandbox` (989) -> can reach a 0660 root:nox-sandbox socket
- no `/dev/vsock` on the host (host-side noxtts would use the Unix socket anyway; forced now)
- RAM 30 GiB, ~13 GiB available; / 90 GiB free; Python 3.13.5
- NoxChat at 36b21e8 == origin/main; local edits: config.yaml (image_gen.studio_model), 2 doc-studio agent .md (adopted, see dev)
- services active: noxchat, agy-openai-bridge, nox-sandbox; no nox-tts yet
- apt: bubblewrap 0.12.0, ffmpeg 7.1.5 installed; espeak-ng, flite NOT installed
- host.yaml (/etc/nox-sandbox/host.yaml): agent_vsock_port 52, allowed_uids [1000], max_frame_bytes 128 MiB, exec_timeout_s 3600; no tts_* keys yet

## tp1 — changes made
- apt: espeak-ng, flite, python3-venv
- user `nox-tts` (system, uid 999, primary group nox-sandbox, no home, nologin)
- /opt/nox-tts (root 0755) + /opt/nox-tts/venv: torch 2.15.0+cpu, sherpa-onnx 2.0.3, pocket-tts 4.0.1, numpy 2.5.5, PyYAML 6.0.4, huggingface_hub
- /var/lib/nox-tts/models (root 0755): sherpa models via fetch_models.py; HF cache for pocket-tts in models/hf
- /tmp/fetch-models.log, /tmp/apt-tts.log (logs)

## tp2
(not involved)
