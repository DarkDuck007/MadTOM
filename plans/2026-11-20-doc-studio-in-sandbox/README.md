# Document Studio inside the chat sandbox (steps 2 + 3)

## Problem
Nox has two separate file worlds. The sandbox keeps files at `/data` in the chat VM. Document Studio keeps them in a host job folder, works in a bwrap jail, and returns `DOC-` handles. Nothing crosses between them automatically, and the guides never mention each other.

Example from tp1, 2026-11-20 17:01–17:15: Nox plotted a chart in the sandbox and started a PDF job. It then used `steer` to say "add it as appendix one", but `steer` carries text only, so the chart never reached the studio.

## Goal
- **One file space per chat.** Studio jobs run inside the chat's VM, under `/data/studio/<job>/`.
- **Files move freely.**
  - Nox can hand sandbox files (`/data/...`) to `create_document` and to `document_job reply|revise|steer`.
  - Finished documents also land in `/data/documents/`.
- **Self-service documents.** Nox can make simple documents in its own sandbox with the same `noxdoc` toolkit, via a `noxdoc` CLI. Designed reports and decks still go to the studio.
- **Security.** Untrusted documents are parsed and rendered in the chat's Firecracker VM, not in a bwrap jail on the shared host kernel.

## Design: the host job folder stays as a mirror
The studio agent (agy, through the bridge) and the MCP server `services/nox-doc-tools/server.py` stay on the host. In VM mode, only command execution moves:

```
agy studio agent ──MCP──► server.py (host)
                            │ bookkeeping on the host job folder (unchanged):
                            │   hashes, listings, submit checks, result.json
                            │ executor = bwrap (today) | vm (new)
                            ▼
                     vmexec.py ── unix socket ──► nox-sandbox ──vsock──► chat VM
                       push:  host inputs/ + work/ai/ + referenced workspace files → /data/studio/<job>/
                       run:   the same argv as the jail, with /work = /data/studio/current
                       pull:  changed work/ + output/ files → host job folder (bytes only, never parsed)
```

- **Why a mirror:**
  - agy's `read_file` needs absolute host paths for previews.
  - NoxChat and the bridge read `result.json` and outputs from the host folder.
  - All of `server.py`'s bookkeeping keeps working as is.
  - The host only stats, hashes and copies bytes. It never parses them, the same rule as today.
- **`/work` inside the guest:** the rootfs carries a symlink `/work → /data/studio/current`. Each exec takes a lock (`flock /data/studio/.lock`) and points `current` at its job. Task code and `noxdoc` therefore see the same `/work` paths as in the jail, so no path rewriting is needed for `/work/...`.
- **Host absolute paths** passed by the agent are rewritten before running:
  - the job folder path becomes `/work`;
  - workspace and artifacts paths are pushed on demand to `/work/.ws/<i>/…`.
- **Choosing the executor:** NoxChat picks it per job (`doc_gen.executor: auto|vm|bwrap`). `auto` picks `vm` when the chat may use the sandbox and the service is healthy, and `bwrap` otherwise. A VM failure during a job becomes a tool error; there is no silent switch.

## Phases
1. **Image** (`services/nox-sandbox-image`):
   - add chromium and fonts (Vazirmatn, Inter, Liberation, Noto Color Emoji, fontconfig);
   - add `qrcode`;
   - bake `/opt/noxdoc`, `/opt/icons` and the prebuilt matplotlib cache `/opt/mplcache`;
   - add the `/work` symlink;
   - put `noxdoc` on the user `PYTHONPATH`.
2. **VM executor** (`services/nox-doc-tools`): `vmexec.py`, and `server.py` refactored around an executor. Add the `noxdoc` CLI for step 3.
3. **NoxChat and bridge:**
   - executor choice and the new registry fields;
   - `/data/...` inputs;
   - `inputs` on `document_job`;
   - copy finished documents to `/data/documents/`;
   - keep-N pruning of `/data/studio`;
   - config, Studio monitoring, guides.
4. **Tests** (mine): a fake sandbox that runs the guest side in a temp directory, driving the real `vmexec`, plus NoxChat unit tests.
5. **Deploy and E2E on tp1:** rebuild the image, raise chat VM memory, run the live studio job in VM mode and replay the chart-into-appendix scenario.

See `contracts.md` for exact interfaces and `progress.md` for status.
