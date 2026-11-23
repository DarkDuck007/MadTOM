# SlideForge probe and plugin: progress

## Phases
- [x] 1. Proto and plan (this folder)
- [x] 2. Auth hardening in `pkg/noxchat/auth` (replay cache, authorized keys, `-insecure-dev`, persistent cert), with tests
- [x] 3. `cmd/slideforge-probe` + `pkg/slideforge/server`, build and deploy specs
- [ ] 4. Python control server (SlideForge repo)
- [ ] 5. Studio plugin (agy gemini-4-neon-medium; VM tests by me)
- [ ] 6. Documentation
- [ ] 7. Probe deployed on tp2

## Log
### 2026-11-24
- Plan written alongside SlideForge v1. The bot core, bridge and tools are in progress in the SlideForge repo.
- tp2 avahi was fixed first, so `dani-tp2.local` resolves for mDNS discovery. See `Devices/tp2/configurations/avahi-mdns.md`.
- My tests first: `pkg/noxchat/auth/hardening_test.go` (replay, verifier, authorized keys, startup policy, persistent TLS) and `pkg/slideforge/server/server_test.go` (allowlist, Call semantics, fake bot over the frame protocol, record conversion).
- Delegated to codex gpt-6.2-sol, J4 (job e4fdec9e, 21 min). Reviewed it and reran `go vet` and `go test -race` on `./pkg/noxchat/... ./pkg/slideforge/... ./cmd/...`, all green.
  - `auth`: `ReplayCache` (heap-bounded, window 10 min ≥ the ±300 s validity span), and `Verifier` (checks the signature first, then records the replay, so failed attempts never burn a nonce). Also `LoadAuthorizedKeys` (accepts `noxchat-ed25519-pub:` and `ssh-ed25519`), `StartupPolicy`, `LoadOrCreateTLS` (ECDSA P-256, persisted, regenerated if corrupt or expiring), `NewEphemeralTLS` and `LoadProbeVerifier`.
  - `noxchat-probe` changes:
    - it uses the Verifier, and the dead bearer fallback is gone;
    - new flags `-authorized-keys`, `-insecure-dev` and `-state-dir`;
    - the unit gains `StateDirectory`.
  - tp1 compatibility:
    - the `-auth-key-file .env` path is unchanged;
    - the C# client signs every call with a fresh GUID nonce, so the replay cache is compatible.
  - The bridge client was rewritten for cancellable writes, `Connected()`, fail-fast pending RPCs on disconnect, and the typed `RPCError`/`ErrTimeout`. Error strings are unchanged.
  - The spool and chunker changes are additive (`QueryLogChunkPage`, `SetMaxBytes` with a strict quota, `ChunkStats`, `Flush`, `CompressPayload`), and NoxChat paths behave the same.
  - New: `proto/madtom/v1/slideforge.proto` plus generated stubs, `pkg/slideforge/server`, `cmd/slideforge-probe`, `systemd/slideforge-probe.service`, and `build.sh`/`deploy.sh` entries (`slideforge:sf-probe`).
  - Telemetry plugin csproj: `slideforge.proto` is excluded from its `v1/*.proto` wildcard.
- Contract §3 now matches the shipped flags.
- Phase 4 (Python control RPCs): the tests are written in SlideForge as `tests/test_control_rpc.py`, and `ControlRpc.METHODS` equals the probe allowlist.
