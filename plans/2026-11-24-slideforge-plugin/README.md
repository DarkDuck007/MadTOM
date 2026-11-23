# SlideForge monitoring: Go probe and MADTOM Studio plugin

## Context
SlideForge is a separate Telegram bot for document generation, built from NoxChat's Document Studio. It lives in the `SlideForge` submodule, which is a private repo with its own plan in `SlideForge/plans/2026-11-24-slideforge-v1/`. It runs on **tp2**.

This plan covers only the MADTOM side:
- a Go probe `slideforge-probe`;
- a gRPC service `SlideForgeControlService`;
- hardening of the shared probe auth;
- a MADTOM Studio plugin "SlideForge" for deep monitoring and administration.

## Decisions
- **Reuse the NoxChat probe libraries:** `pkg/noxchat/{bridge,auth,buffer,chunker,spool}`. The bot speaks the same frame protocol as NoxChat's control channel: `[kind u8][len u32 BE][JSON]`, on 127.0.0.1:50058.
- **A typed core plus a generic JSON call.** Status, streams and telemetry ranges are typed, as in NoxChat. Admin RPCs go through one `Call{method, payload_json}`, checked against a method allowlist in the probe.
  - Why: NoxChat's proto has about 60 typed admin RPCs (about 1.5k lines), and each one needs Go and C# plumbing.
  - With the JSON call, a new bot RPC needs only a Python handler, a probe allowlist entry and a C# view model.
  - The C# side still deserializes into typed records.
- **Auth hardening.** The changes go into the shared `pkg/noxchat/auth` and are backward compatible, so NoxChat gains them too:
  - Nonce replay cache: `(nonce, ts)` is remembered for the skew window, and a replay is rejected.
  - Optional authorized-keys file with more than one key, in the same `noxchat-ed25519-pub:` format.
  - The probe persists its self-signed TLS key and certificate in its state dir. The plugin pins the SHA-256 fingerprint on first connect (TOFU, like SSH `known_hosts`) and refuses a changed certificate until the user re-trusts it in the node manager.
  - Token-less mode is refused unless `-insecure-dev` is given.
- **Ports:** probe gRPC :50057 on tp2. mDNS `_slideforge-probe._tcp`. Studio connects over the LAN (`dani-tp2.local:50057`), or through the ssh LocalForward `50057`.
- **Delegation:**
  - Go probe and auth: codex gpt-6.2-sol.
  - Plugin UI: **agy gemini-4-neon-medium** (user instruction).
  - I write the proto, the Go auth tests and the C# view-model tests.

## Plugin tabs (`MADTOM_DOTNET/src/Plugins/SlideForge/MADTOM.Plugins.SlideForge`)
1. **Overview & Telemetry:**
   - queue, active, waiting-for-answer;
   - jobs per day; failure rate; average duration;
   - weekly quota burn;
   - bridge, sandbox and agy health;
   - Python runtime charts (`MetricHistoryChartControl`).
2. **Jobs:**
   - a live list with filters;
   - a detail timeline (events, studio questions and answers, notes, tool steps);
   - spec and assembled-prompt viewer;
   - previews and output download;
   - cancel and extend.
3. **Users & Quota:** search; usage over 7 days; set the weekly limit; ban or unban.
4. **Guides & Tips:** edit `guides/*.md` and `tips.yaml`; preview the assembled prompt for a sample spec.
5. **Sandbox:** the same passthroughs as the NoxAI Sandbox tab (VMs, files, runs, image).
6. **Logs & API:** live and spooled logs, plus agy tool/model steps.
7. **Config:** the effective `config.yaml`, read-only, with secrets redacted, and every limit listed.

## Phases
1. Proto and plan (this folder).
2. Auth hardening in `pkg/noxchat/auth`, with tests. NoxChat keeps working (regression run of the noxchat-probe tests).
3. `cmd/slideforge-probe` and `pkg/slideforge/server`; `build.sh`; `deploy.sh` `PROJECT_SPECS`.
4. Python control server in SlideForge (separate repo).
5. Plugin: VMs and views; registration in `App.axaml.cs`, `MADTOM.Studio.csproj`, `.sln`/`.slnx`, `build.sh` `TEST_PROJECTS` and `publish.sh`.
6. Docs: `Documentation/Plugins/SlideForge/README.md`, `cli-and-scripts.md`, the README index and the OrderWeight table.
7. Deploy the probe to tp2 (systemd `slideforge-probe`), with the log in `Devices/tp2/configurations/slideforge.md`.

## Verification
- Go: `go test ./pkg/noxchat/... ./pkg/slideforge/...`, covering replay rejection, authorized keys, `-insecure-dev` gating and the method allowlist.
- .NET: `./build.sh` with 0 warnings; plugin view-model tests.
- Live: the Studio plugin is connected to tp2 during the SlideForge end-to-end runs.
