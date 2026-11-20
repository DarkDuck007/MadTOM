# Nox Sandbox (chat-bound VMs), Preprocessors & Scheduler — Plan

## Context
Nox (Python, `NoxChat/src/`) has no code execution of its own. Only the Document and Image Studio sub-agents can run Python and open many file types, inside bwrap jails (`services/nox-doc-tools/server.py`). We want:
1. **A chat-bound sandbox VM** where Nox can do real file work. It can open users' docx/xlsx/pdf, edit them, keep markdown notes, and send results back. Its disk and Python session persist per chat. Nox may also choose a throwaway single-use VM.
2. **Preprocessors.** These are Nox-authored, chat-bound scripts that intercept incoming messages or invocations (text, or audio after STT). They can handle a message in code instead of calling the LLM.
3. **Scheduling.** A tool whose timers fire into Nox or into a preprocessor.

Hard requirements:
- No network at all.
- **Chat isolation is the security boundary.** A compromised chat VM must only ever affect that chat, never the host and never other chats.

Decisions made:
- The VM manager is a Go service.
- Owner and chat admins are immune to preprocessors that drop or replace Nox.
- Anyone can ask for a preprocessor and Nox decides, within guardrails.

## Technology choice
tp1 is bare-metal Debian 13 with `/dev/kvm`, 12 cores and 30 GB RAM.

**Firecracker stays the choice.** It runs under `jailer` (chroot, cgroups v2, seccomp, unprivileged uid), with **no virtio-net device**, **MMDS disabled**, and vsock as the only channel.
- gVisor would be easier to image, but it is weaker and slower on syscalls.
- bwrap shares the host kernel, so it is too weak for persistent, LLM-driven code that runs on every message.

## VM model

| | **Chat VM** (default) | **Temp VM** | **Pre VM** (preprocessors) |
|---|---|---|---|
| Lifetime | Per chat; stays warm `sandbox.warm_seconds` after last use, then hibernates | One call, then destroyed | One call, from the warm pool |
| Disk | Persistent per-chat volume `/data` (quota) | None; files copied in and out explicitly | None |
| Python | Persistent kernel sessions (REPL state survives between calls and across hibernation) | Fresh | Fresh, stdlib only |
| Use | Notes, user files, multi-step work | Suspicious files, risky one-offs, parallel work | Every-message hooks (low latency, tamper-proof) |

### Chat VM lifecycle
`cold → booting → warm ⇄ busy → (idle warm_seconds) → hibernated → (hibernate_ttl) → cold`

- **Hibernate** means pausing the VM and taking a Firecracker snapshot (memory plus device state) to `/var/lib/nox-sandbox/chats/<chat>/snap/`. Resume restores the snapshot, so Python sessions survive.
  - Before snapshotting, inflate the balloon device so the memory file stays small.
  - After `hibernate_ttl` (config), the snapshot is deleted. The disk stays and the Python session is lost; Nox is told "session was reset".
- **Lease model.** A Nox turn takes a lease on the chat VM while the turn runs, and a background job holds its own lease. The idle timer only starts when no lease is held. This stops the VM from dying mid-turn.
- **Rootfs** is read-only, with a tmpfs overlay for system paths. **Only `/data` persists.** Every boot or resume therefore starts with a clean OS, and malware can only persist as files in `/data`, inside its own chat.

### Files in and out
- **Host workspace (`media/workspace.py`, `file_store.py`) stays canonical for Telegram files.**
- Tool args `inputs: [workspace refs]` copy those files into `/data/inbox/`.
- Nox calls `export(paths)` (or writes to `/data/outbox/`) to pull files back into the host workspace. From there they can be sent with the existing `<document>`/`<image>` tags.
- There is no two-way sync.

### Nox tools (small schemas; long guide in `persona/guides.py` `tool_guide`)
- `sandbox_run(code | shell, session?="main", vm?="chat"|"temp", inputs?, export?, timeout?, background?)`
  - `background=true` returns a job id. Completion arrives via `runtime.notify_task_result` (the existing wake path).
- `sandbox_manage(action: status|ls|reset_session|kill|snapshots|restore_snapshot)`

## Loopholes / problems found, and fixes

### Escape paths that bypass the VM (most important)
1. **Host-side parsing of chat-produced files.** Exported files get parsed on the host:
   - `src/control/rpc_handlers.py:1618-1690` runs `soffice` and `pdftoppm` unsandboxed;
   - `src/media/pdf_render.py` runs poppler unsandboxed;
   - other paths run Pillow thumbnails and ffmpeg.

   A malicious docx/pdf crafted inside an infected VM could exploit those parsers **on the host**, which skips the VM entirely.
   **Fix:** every host-side parse of chat files (previews, thumbnails, PDF render, conversions) runs inside a temp VM, or at minimum the existing bwrap jail. The host itself only does magic-byte sniffing and size checks.
2. **Mounting chat disk images on the host.** A loop-mounted ext4 image crafted by the guest is a host-kernel attack.
   **Fix:** the host *never* mounts chat volumes. All file access goes through the guest agent. Backups copy the raw image byte-for-byte.
3. **Untrusted vsock protocol.** The guest agent must be treated as hostile.
   **Fix:** the Go manager uses length-prefixed frames with hard size caps, strict JSON schemas, per-message timeouts and rate limits. Nothing the guest sends is ever interpreted as a path or command.
4. **Export path traversal.** Risks include `../`, absolute paths, symlinks, hardlinks, device files and FIFOs.
   **Fix:** no tar extraction. The agent sends `(name, bytes)` per file. The host sanitizes the basename, accepts only regular files, caps count and size, and writes with `O_NOFOLLOW` into the chat's workspace directory only.
5. **Guest-initiated vsock / MMDS.** Firecracker maps guest connects to `<uds>_<port>`.
   **Fix:** never listen on those paths, and never enable MMDS. Config validation refuses to start if a network interface is defined.

### Cross-chat leakage
6. **Wrong chat routing.** `chat_id` must come from host tool context, never from LLM args. `tools/registry.py` already makes context override args; keep `chat_id` as a hidden param. The manager keys every VM, volume, snapshot and job by chat_id. Add a test asserting that no tool accepts a chat id from args.
7. **Shared snapshot cloning.** Pre and temp VMs restored from one base snapshot share RNG state, Python hash seeds and the stale clock.
   **Fix:** attach a virtio-rng entropy device. On every restore the agent reseeds the RNG (host-supplied seed via `RNDADDENTROPY`), sets the clock from the host, and restarts any long-lived RNG consumers.
8. **Telegram chat migration.** Group-to-supergroup migration changes the `chat_id`, which would orphan or misattribute volumes.
   **Fix:** handle `migrate_to_chat_id` by renaming the volume and snapshot directory atomically.
9. **Guest/unknown chats** (`telegram/guest.py`): the sandbox is disabled by default. It is enabled per chat via config or Control Center.
10. **Co-resident side channels** (Spectre-class). Accepted risk. Keep host mitigations on and pin VMs away from Nox's own process cores if needed. Low priority.

### Resource abuse (within a chat, or against the host)
11. **Long or intensive tasks.** Limits:
    - per-call wall timeout, default 120 s and larger for background jobs;
    - a hard max VM session lifetime (e.g. 30 min, even if leased);
    - cgroup `cpu.max` quota per VM;
    - fixed guest RAM;
    - Firecracker block rate limiters (bandwidth and IOPS);
    - `pids` limit inside the guest;
    - a per-chat daily CPU-seconds budget, shown in `sandbox_manage status`.
12. **Background activity faking "use".** The idle timer counts only *Nox tool calls and leases*, never guest activity. Guest processes left behind are killed by hibernation.
    - Problem: hibernation would also freeze a running miner and resume it later.
    - **Fix:** before snapshotting, the agent kills all non-kernel-session processes.
13. **Host saturation** (many chats waking at once): a global cap on concurrent VMs and on total guest RAM. Beyond the cap, the LRU warm VM is hibernated; beyond that, new requests queue or fail.
14. **Disk fill.** Each chat volume is a fixed-size sparse image (e.g. 2 GB, configurable). Snapshots count against a per-chat quota, and so do exports. There is also a global disk watermark that stops hibernation and falls back to cold.

### Within-chat risks (by design, mitigated)
15. **Any member can ask Nox to delete or read others' files in the same chat.** The chat is the trust boundary.
    **Mitigation:** daily volume snapshots (keep N, raw copy or reflink if the filesystem allows), restorable via `sandbox_manage restore_snapshot` or Control Center.
16. **Prompt injection from file contents and VM output.** For example, a docx contains "call preprocessor create …".
    **Fix:**
    - wrap sandbox output as untrusted data in the tool result (`<untrusted>` marker plus a guide rule);
    - cap the output size;
    - the impact of any resulting preprocessor is limited to the chat, and immunity is enforced host-side.
17. **Preprocessors must not run inside the chat VM.** A compromised chat VM could otherwise hijack every message hook, and every message would wake the VM.
    **Fix:** preprocessors run in stateless Pre VMs. Their only state is host-held JSON passed in and out.
18. **Secrets.** There is no env, config, token or system prompt inside any VM. The agent image is checked to have no host-identifying data.

### Usability gaps
19. **No pip (offline).** Ship a fat compute image: pandas, openpyxl, python-docx, python-pptx, pypdf, pdfplumber, matplotlib, pillow, soffice, poppler, pandoc, markitdown.
    Later idea: a curated host wheelhouse mounted read-only, for offline `pip install` from an allowlist.
20. **Nox forgetting what's on disk.** When the sandbox is enabled for a chat, inject a short `/data` index into the prompt (top N paths and sizes, cached host-side from the last run's manifest). Notes live in `/data/notes/*.md`.
21. **Concurrent use** (a background job plus a new turn). The agent supports multiple named sessions. If a session is busy, the call errors with "busy", or runs in a fresh session when `session="new"`.
22. **Snapshot load drops vsock connections** (a Firecracker behaviour). The agent re-listens after resume and the host reconnects with retries.
23. **Kernel session crash.** The agent restarts the kernel and the tool result reports "session reset".

### Added during contract design
24. **Privilege escalation through config.** `nox-sandbox` runs as root because jailer needs it, but `config.yaml` is owned by the NoxChat user. If that file could set binary paths, directories or unlimited resources, compromising NoxChat would mean compromising root.
    **Fix:** paths, uids and hard ceilings live in root-owned `/etc/nox-sandbox/host.yaml`. `config.yaml` values are clamped to those ceilings.
25. **Who can reach the API socket.** The fix has three parts:
    - the socket file has mode 0660 and group `nox-sandbox`;
    - the server checks the peer uid with `SO_PEERCRED` against `host.yaml` `allowed_uids`;
    - the API never takes host file paths, so it cannot be used as a confused deputy to read root-owned files. Inputs travel as bytes.
26. **Reading a cold chat volume** (for the Studio browser or for exports while hibernated) requires a VM, since the host never mounts volumes.
    **Fix:**
    - boot an *inspector* temp VM with the volume attached read-only, mounted `ro,noload` so no journal replay writes;
    - before hibernation, `prepare_hibernate` runs `sync` so the on-disk state is consistent;
    - while an inspector holds a volume, the chat VM cannot start; it waits;
    - a host-side manifest cache, refreshed after every exec and at hibernation, answers listings without booting anything.
27. **Rebuilding the base image** needs network on the host (apt/pip). The build runs on the host as a dedicated build step, never inside guests.
    - The admin's approval is the supply-chain gate.
    - Package names are validated against strict regexes, and version pins (`name==1.2`) are allowed.
    - The build log is shown in Studio.
    - Old image versions are kept until no snapshot references them.

## Architecture

```
NoxChat (Python) ──unix socket──► nox-sandbox (Go, systemd; jailer drops privs per VM)
   sandbox_run / preprocess / schedule      ├─ chat VMs (persistent volume + snapshots)
                                            ├─ temp pool, pre pool (snapshot-restored, single use)
                                            └─ vsock ⇄ guest agent (python): sessions, exec, files
```

### Components
1. **`nox-sandbox`** Go service: `MADTOM_GOLANG/cmd/nox-sandbox/`, `pkg/noxsandbox/`, using `firecracker-go-sdk`.
   - Parts: lifecycle state machine, leases, pools, quotas, hibernation, daily volume backups, telemetry.
   - Socket: `/run/nox-sandbox/api.sock`, mode 0660 for group `noxchat`.
   - Unit: `MADTOM_GOLANG/systemd/nox-sandbox.service`.
2. **Guest image**: `NoxChat/services/nox-sandbox-image/`.
   - `build.sh`: debootstrap rootfs (ext4, read-only), pinned Firecracker guest kernel, compute packages.
   - `agent.py`: vsock server; Jupyter-style persistent sessions via `code.InteractiveInterpreter` in subprocesses; file ops; reseed; pre-hibernate cleanup.
   - Base snapshot step for the pools.
3. **NoxChat `src/sandbox/`**: `client.py` (async unix socket), `tools.py` (`sandbox_run`, `sandbox_manage`), the disk-index prompt injection, and export into the workspace.
   - Add per-tool timeouts in `tools/registry.py`; the current global default is 20 s.
4. **Preprocessors `src/preprocess/`.** Unchanged from the earlier design:
   - Contract: `handle(event, state) -> (actions, state)`.
   - Actions: pass, drop, reply, react, sticker, invoke(note).
   - Hooks in `telegram/runtime.py` `accept()` (after the trigger is computed) and `_audio_complete()` (after `spoken_trigger`). They never fire on bot messages.
   - Engine behaviour: priority order, fail-open (~300 ms budget), auto-disable after 5 errors, send rate cap, owner/admin immunity, `via_preprocessor` tagging.
   - Tables: `preprocessors` and `preprocessor_log`.
   - Tool `preprocessor`: create/update/test/list/show/enable/disable/delete/logs, with a visible chat notice on create and a cap of 10 per chat.
   - Pass `requester_user_id` into the tool context (`dispatcher.py:1202`).
5. **Scheduler `src/schedule/`.**
   - `schedules` table and an asyncio loop re-armed at startup (pattern: `media/doc_jobs.py` recover).
   - `croniter` for cron expressions.
   - Targets: `nox` (via `notify_task_result`) or `preprocessor` (a `kind="schedule"` event).
   - Tool `schedule`: create/list/cancel.
6. **Admin surfaces.**
   - RPCs in `src/control/rpc_handlers.py`: VMs (status, kill), volumes (usage, snapshots, restore), preprocessors, schedules.
   - Telegram `/preprocessors` and `/sandbox` for admins.
   - C# NoxAI plugin tabs, following the Tips-tab pattern. Done by me or agy, never codex.
7. **Hardening of existing host parsers** (loophole 1): route `rpc_handlers.py` previews and `pdf_render.py` through temp VMs, or bwrap as an interim step.

### config.yaml (new `sandbox:` section)
- `enabled_chats`
- `warm_seconds`
- `hibernate_ttl`
- `max_session_lifetime`
- `chat_vm`: {mem_mb, vcpus, cpu_quota, disk_gb, iops/bw limits}
- `temp_vm`: {…}
- `pre_vm`: {…}
- `global`: {max_vms, max_guest_mem_mb, disk_watermark}
- `daily_cpu_seconds`
- `backups_keep`

## Phasing
1. **Sandbox core.** Image, Go manager (chat VM, temp VM, leases, warm/idle, quotas), `sandbox_run`/`sandbox_manage`, export path, disk index. Then hibernation (snapshot/resume, reseed) and daily backups.
2. **Host parser hardening** (loophole 1). Ships with or right after phase 1, since exports make it reachable.
3. **Preprocessors** (Pre VM pool, engine, hooks, tool, immunity, admin command and RPCs).
4. **Scheduler.**
5. **Control Center tabs.**

Delegation: codex for Go boilerplate and the image `build.sh`; I review. I write all tests myself.

## Critical files
- New: `MADTOM_GOLANG/cmd/nox-sandbox/`, `MADTOM_GOLANG/pkg/noxsandbox/`, `MADTOM_GOLANG/systemd/nox-sandbox.service`
- New: `NoxChat/services/nox-sandbox-image/`
- New: `NoxChat/src/sandbox/`, `NoxChat/src/preprocess/`, `NoxChat/src/schedule/`
- Modify:
  - `NoxChat/src/telegram/runtime.py`
  - `NoxChat/src/telegram/dispatcher.py`
  - `NoxChat/src/tools/registry.py`
  - `NoxChat/src/db/schema.py`
  - `NoxChat/src/config.py`
  - `NoxChat/src/persona/guides.py`
  - `NoxChat/src/persona/prompt_builder.py` (disk index)
  - `NoxChat/src/control/rpc_handlers.py`
  - `NoxChat/src/media/pdf_render.py`
  - `NoxChat/docs/deployment.md`

## Verification
- **Isolation (on tp1):**
  - in the guest, socket connect, DNS and `/sys/class/net` show nothing but lo;
  - no host paths are visible;
  - chat A's VM cannot see chat B's files: run a test with two chat ids;
  - a crafted export (symlink, `../`, 10k files, 5 GB) is rejected;
  - garbage vsock frames do not crash the manager (fuzz test in Go);
  - the host never mounts volumes (grep plus an integration test).
- **Lifecycle:**
  - a variable set in session "main" survives warm idle and hibernate/resume;
  - after `hibernate_ttl` the session is reported reset and `/data` is intact;
  - a VM is never killed mid-turn while leased;
  - a miner loop is killed at hibernation;
  - CPU budget exhaustion is reported;
  - the global VM cap hibernates the LRU VM;
  - two pool VMs get different `os.urandom` values after restore.
- **E2E in a test chat:**
  - send an xlsx, ask Nox to add a column and send it back;
  - "save a note about X", restart noxchat, "what was my note?";
  - a suspicious docm goes to a temp VM.
- **Preprocessors and scheduler.** pytest with a mocked sandbox client covers ordering, immunity, fail-open, expiry, rate limits and the fake-clock scheduler. Live checks:
  - "ignore @X" means no `llm_call_log` entries for X;
  - "👍 to X for 2h" expires;
  - voice mentions are handled;
  - schedules survive a restart.

## Additions after user review (2026-11-20)

1. **Every limit, quota, cap and budget is configurable** in NoxChat `config.yaml` under `sandbox:`, `preprocess:` and `schedule:`. Keys and defaults are listed in [contracts.md](contracts.md#config). A second, root-owned `/etc/nox-sandbox/host.yaml` holds the binary and directory paths plus **hard ceilings**. The root service clamps `config.yaml` values to those ceilings. This closes a privilege-escalation hole (loophole 24): `config.yaml` is writable by the NoxChat user, so letting it choose binaries or unlimited memory for a root service would hand that user root.
2. **MADTOM Studio monitoring.** The NoxAI plugin gets a **Sandbox** tab:
   - a list of hot VMs (warm/busy) and cold VMs (hibernated, or just a disk);
   - for each VM: state, RAM, disk used/quota, snapshot size, CPU-seconds today, sessions and jobs;
   - a file browser per chat volume;
   - limited viewing: plaintext (size-capped), plus **pre-rendered safe images**. The guest renders a supported file to raw RGBA pixels, the Go host encodes the PNG with Go's memory-safe `image/png`, and Studio never decodes a file produced by the guest. Formats: images, pdf, docx/xlsx/pptx/odt via soffice, svg.
   - admin actions: kill, hibernate, restart kernel, restore backup, wipe volume;
   - the library-request queue (approve/reject) and the base-image build status.
3. **Library requests.** New tool action `sandbox_manage(action="request_library", packages, reason)`:
   - requests are stored in NoxChat (`sandbox_library_requests`) and shown in Studio for the admin to approve;
   - approved packages go to `nox-sandbox` (`PUT /v1/image/packages`), which rebuilds the base image at the configured `rebuild_window`, or immediately if the admin presses "Rebuild now";
   - new boots use the new image version; running or hibernated chat VMs keep their pinned image version until they expire or Nox restarts them (`sandbox_manage restart_vm`);
   - the requesting chat gets a task-result notice when its package becomes available.
4. **Nox can restart the Python kernel** (`sandbox_manage reset_session`) and the whole VM (`restart_vm`).
5. **Compact tool definitions.** `sandbox_run`, `sandbox_manage`, `preprocessor` and `schedule` each get a one-line description and a minimal schema. The full rules live in `tool_guide` topics (`sandbox`, `preprocessors`, `schedule`) in `persona/guides.py`. Nox reads a topic when it needs it, and `remind_on` is only set on rarely used tools.
6. **No firecracker-go-sdk.** The Go service talks to the Firecracker REST API over its unix socket with `net/http` (only about 10 endpoints are needed) and runs `jailer` with `os/exec`. This keeps the dependency tree small.
7. **Delegation.**
   - agy: Gemini 4 Neon (medium) by default; Argon (medium/high) for heavier work; all MADTOM Studio UI goes through agy.
   - codex: moderate effort, for Go and Python logic.
   - I do the final review and write all tests.
   - Delegates never touch tp1. Deployment and on-host testing on tp1 are mine only.

### Loopholes found during implementation review
28. **Library requests → root on the build host.** `build.sh` ran `pip install` as root in a chroot with /proc, /sys and /dev mounted. A source-only package runs `setup.py`, and root inside a chroot can escape it. **Fix:** `pip install --only-binary=:all:` (no install-time code). Apt packages come from the signed Debian mirror only. Future work: run the build itself inside a Firecracker VM.
29. **Shared jail uid.** Every Firecracker process ran as uid 64000. If one VMM is compromised (a guest escape into the VMM), seccomp still blocks ptrace and process_vm_*, but chats would share a uid. **Fix:** per-slot uids from `jail_uid_base` + index, plus `--new-pid-ns` and cgroup `memory.max`/`pids.max` per VM.
30. **Guest-supplied `sanitized` flag.** The agent's `get_files` reply could carry `"sanitized": true`. **Fix:** `cleanFiles` always overwrites it, and only the host decoder sets it.
