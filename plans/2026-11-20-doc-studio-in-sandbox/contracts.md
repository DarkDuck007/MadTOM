# Contracts

## 1. Registry fields (NoxChat → bridge → `<sid>.json`)
NoxChat's `POST /v1/doc-studio/jobs` body gains three fields:
- `executor`: `"bwrap"` or `"vm"`. Default `"bwrap"`.
- `chat_id`: int. Required when the executor is `vm`.
- `vm`: object. The bridge passes it through to the registry unchanged, in the same place as the job fields:
  ```
  {"socket": "/run/nox-sandbox/api.sock", "sync_max_file_bytes": 52428800,
   "exec_margin_s": 15, "keep_jobs": 5, "lock_wait_s": 60}
  ```
  The values come from `doc_gen.sandbox.*` in config.yaml (section 5).

The bridge validates the types and refuses `executor="vm"` without an int `chat_id`. It writes all three into every registry rewrite, and `server.py` reads them from the registry only, never from tool arguments.

## 2. Guest layout
- `/data/studio/<job_id>/{inputs,work,output}`: the job's tree. It persists in the chat volume, so Nox can see it.
- `/data/studio/current`: a symlink to `<job_id>`, rewritten under the lock.
- `/data/studio/.lock`: an flock file. Every studio exec holds it for its whole run.
- `/work`: a symlink baked into the rootfs, pointing to `/data/studio/current`.
- `/data/studio/<job_id>/.exec/`: wrapper scratch (state.json, result.json, stdin, out/err). It is never pulled except for `result.json`.
- `/data/studio/<job_id>/.ws/<i>/...`: workspace and artifacts files pushed on demand. Never pulled.
- `/data/documents/`: finished primary documents (`<safe title>.<ext>`; on a name clash, append ` (2)`, ` (3)`, …).
- `/opt/noxdoc` (package dir `/opt/noxdoc/noxdoc`), `/opt/icons/lucide.json`, `/opt/mplcache`: baked into the image.
  - `/opt/noxdoc/VERSION` holds the sha256 of the noxdoc tree as it was at build time.
- The user environment of every exec has `PYTHONPATH=/opt/noxdoc` and `NOX_DOC_ICONS_DIR=/opt/icons`.

## 3. VM executor (`services/nox-doc-tools/vmexec.py`)
Every call uses the nox-sandbox HTTP API over the unix socket, with `vm="chat"`, `kind="shell"` and `requester="doc-studio:<job_id>"`. Session name: `studio-<job_id>-<slot>`, where `slot` is the index of the MCP worker thread.

One `run(argv, timeout, limits, stdin, cap)` is two round trips:
1. **Push and exec.** The `inputs` carry new or changed host files:
   - everything under `inputs/` and `work/ai/`;
   - the full `work/` on the first sync of a job (revisions are seeded on the host);
   - workspace and artifacts files referenced in argv or stdin;
   - stdin, as a file.

   Pushes are batched under `MaxInputBytes`; extra batches use a no-op exec. The exec runs the wrapper, which:
   - takes the lock;
   - mkdirs the tree and moves the pushed files into place;
   - repoints `current`;
   - prunes `/data/studio/*`, keeping the `keep_jobs` newest plus the current job;
   - runs argv under `timeout -k 5` and `ulimit` (python: `-v` 3 GiB, `-t` 120, `-f` 100 MiB; browsers: `-t` 180, `-f` 200 MiB), with cwd `/work`;
   - diffs `work/` and `output/` against `.exec/state.json`, skipping `.ws`, `.exec`, `__pycache__` and `.cache`;
   - writes `.exec/result.json` = `{rc, seconds, out_tail, err_tail, changed:[rel], deleted:[rel], skipped:[rel]}`. `out_tail` is at most `cap` characters (default 40000); `err_tail` is at most 8000.

   The exec request exports `.exec/result.json`, so it comes back in the same response.
2. **Pull.** Export the changed paths in batches under `MaxExportFiles`/`MaxExportBytes`. A file whose extension the sandbox refuses goes into `skipped`, which means it stays VM-only.
   - Write each file into the host job folder under its rel path, using `O_NOFOLLOW`. Refuse `..`, absolute paths, and any parent that is a symlink. Files larger than `sync_max_file_bytes` are skipped.
   - Remove `deleted` paths from the host mirror (only under `work/` and `output/`).

Host path rewriting covers argv strings and stdin text:
- `str(job_dir)` and `job_dir_raw` become `/work`.
- Each workspace dir and the artifacts dir becomes `/work/.ws/<i>`, and only the referenced files are pushed.
- Results returned by tasks contain `/work/...`; the existing `_jail_paths` maps them to host paths.

Errors raise `ToolError` with short, agent-actionable text:
- sandbox unavailable or forbidden: "the chat sandbox is unavailable: <reason>; ask Nox to retry later";
- busy or lock timeout;
- daily CPU budget exhausted;
- disk quota exceeded.

`list_fonts` and `fc-match` run in the VM when the executor is `vm`.

## 4. noxdoc CLI (step 3, `python3 -m noxdoc ...`)
- `render-pdf <html> <out.pdf> [--previews DIR] [--dpi 80]`: Chromium with the exact flags `server.py` uses, then previews and lint.
- `render-pptx <deck.pptx> [--previews DIR]`: soffice to a companion PDF, then previews and lint.
- `preview <pdf> [--pages 1,3-5] [--dir DIR]`
- `lint <file.pdf|file.pptx>`
- `inspect <file>`

Every subcommand prints one JSON object on stdout. It reuses `noxdoc._tasks` functions and does not duplicate them.

## 5. config.yaml (`doc_gen`)
```yaml
doc_gen:
  executor: auto            # auto | vm | bwrap
  copy_to_sandbox: true     # copy finished documents to /data/documents/ when the chat may use the sandbox
  sandbox:
    keep_jobs: 5            # /data/studio job folders kept per chat
    sync_max_file_bytes: 52428800
    exec_margin_s: 15
    lock_wait_s: 60
```

## 6. NoxChat tool surface
- `create_document.inputs` and `document_job.inputs` (for `reply`/`revise`/`steer`) also accept guest paths `/data/...`.
  - NoxChat exports them through the sandbox client as raw bytes, without parsing.
  - They are staged as `in_NN_<name>` in the host `inputs/` folder and appended to the manifest.
  - In a running job, the message text gets "New input files: inputs/in_05_chart.png (chart.png)" added.
  - The limits are `doc_gen.max_inputs` and the existing byte caps.
- The completion notice for Nox gains `sandbox_path: /data/documents/<name>` when the copy succeeded.
