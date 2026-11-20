# Progress: Nox sandbox

Legend: [ ] todo · [~] in progress · [x] done · [!] blocked

## Phase 1: Sandbox core
- [x] Plan, contracts and config keys (`README.md`, `contracts.md`)
- [x] Go `nox-sandbox` service: delegate job a1fd4643 (codex gpt-6.2-sol), reviewed. Hardening round: per-VM jail uids, `--new-pid-ns`, memory/swap/pids cgroups, `cgroup.kill`. I added host image sanitizing (`manager/sanitize.go`), tolerance for the echoed `session`, guest-error logging and body-less action POSTs.
- [x] Guest image (`build.sh`, `nox-init`, `agent.py`, `fetch-kernel.sh`): delegate job 2131ed7b (agy argon-high), reviewed, 18/18 of my agent tests pass
- [x] NoxChat side: delegate job 6c27ff96 (codex gpt-6.1-sol), reviewed and fixed (sanitized-image-only image store, per-chat library dedupe, approve omitempty); 28 tests of mine pass
- [x] My tests:
  - agent: 18, TCP test mode
  - Go manager: 14 (fakevm, -race)
  - Go API: 3 (SO_PEERCRED, body limits, unknown fields)
  - Go agent client: hostile frames
  - probe wire tests
  - NoxChat: 28
  - Studio Sandbox tab: 10
- [x] Review all diffs
- [~] tp1: install firecracker+jailer, kernel, build image, host.yaml, systemd, smoke tests (mine only)
  - [x] debootstrap installed; Firecracker+jailer v1.18.0 → /opt/nox-sandbox/bin
  - [x] guest kernel: firecracker-ci v1.15 vmlinux-6.1.155 (sha256 e20e46d0…f4f2) → /opt/nox-sandbox/images/vmlinux (+ .config); has VSOCK, BALLOON, HW_RANDOM_VIRTIO, EXT4, OVERLAY, TMPFS
  - [x] rootfs image 2026.11.20-1 built on tp1 (sha256 2e35b967…); manual Firecracker boot: agent up in ~0.95 s, only `lo`, no route/DNS, uid 1000 user code, overlay root, LibreOffice/pandoc/poppler OK, session persistence + timeout reset OK
  - [x] host.yaml (chroot_base under state_dir for hardlinks; jail uid range 64000+64), systemd unit, service smoke over the real API.
    - cold exec 1.3 s, warm exec 14 ms; hibernate → restore 279 ms with the session kept; snapshot 157 MB for a 1 GB VM
    - exports: png sanitized, xlsx bytes, fake png `bad_image`; temp pool works
- [~] Isolation verification on tp1:
  - [x] only lo, network unreachable
  - [x] per-VM uids
  - [x] temp VM has no disk
  - [x] /dev/mem denied
  - [x] fork bomb contained
  - [x] guest OOM → `killed:oom`
  - [x] disk quota holds
  - [x] unlisted chat forbidden
  - [x] cross-chat list empty
  - [x] guest sysctl hardening (dmesg_restrict, kptr_restrict=2, no user namespaces, …): image 2026.11.20-3 built via `/v1/image/rebuild`. Pinning verified: a hibernated chat restores on -1 with its session, and a restarted chat boots -3.
  - [ ] fuzz vs a live agent

## Phase 1b: Monitoring
- [x] Go probe pass-throughs and proto: delegate job 8ad83599 (codex gpt-6.1-sol). Fixed `mtime` to int64 and added `kind` to the preview RPC.
- [x] MADTOM Studio Sandbox tab: delegate job 0bd932ca (agy gemini-4-argon-medium), 0 warnings, 10/10 of my VM tests pass

## Phase 2: Host parser hardening
- [ ] Route `rpc_handlers.py` soffice/pdftoppm previews and `pdf_render.py` through temp VMs (or bwrap)

## Phase 3: Preprocessors
## Phase 4: Scheduler
## Phase 5: Control Center tabs for preprocessors and schedules

## Log
- **2026-11-20**
  - Plan approved with these user amendments: chat-bound persistent VMs; all limits configurable; Studio monitoring; library requests; kernel restart; compact tool definitions.
  - Chose host.yaml/config.yaml split (loophole 24).
  - No firecracker-go-sdk.
  - Started three delegate jobs.
- **Environment:**
  - tp1 is bare-metal Debian 13 with /dev/kvm, 12 cores and 30 GB RAM; docker and bwrap are installed.
  - The dev box also has /dev/kvm.
- 2026-11-20: tp1 root fs is ext4 (no reflink) → backups are sparse copies (`cp --sparse=always`). kvm + vhost-vsock present, cgroup v2 controllers cpu/memory/pids available.
- 2026-11-20 review notes:
  - Sandbox exports reaching `ChatImageStore` would be parsed by Pillow/ffmpeg on the host. Fix: the Go host re-encodes png/jpg/webp with memory-safe Go codecs (`sanitized: true`, contracts §2.1); only those go to the image store.
  - Files users upload on Telegram are already parsed on the host the same way, so phase 2 hardening covers both paths.
  - Agent path checks have a check-then-open race between the root agent and uid-1000 code. Accepted: the impact is guest-local only (tmpfs overlay, VM boundary).
  - Contract fix: `mtime` is an int of unix seconds everywhere.
  - Cleanup before commit: remove `MADTOM_GOLANG/.nox-build-cache/`, the Go cache a worker created.
- 2026-11-20 (cont.):
  - **Loophole 28:** library rebuild → root on the build host via an sdist `setup.py`. Fixed with wheel-only pip. It immediately caught `odfpy` (sdist-only), which now comes from apt as `python3-odf`. A failed build correctly keeps `current`.
  - **Known limit:** no virtio discard, so files deleted in the guest keep their host blocks. Usage is still bounded by `disk_gb` per chat and the watermark.
  - **Next:** NoxChat deploy with `sandbox.enabled` for a test chat (needs a commit + user OK + test chat id); phase 2 (host parser hardening).
