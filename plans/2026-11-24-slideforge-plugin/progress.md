# SlideForge probe and plugin: progress

## Phases
- [x] 1. Proto and plan (this folder)
- [ ] 2. Auth hardening in `pkg/noxchat/auth` (replay cache, authorized keys, `-insecure-dev`, persistent cert), with tests
- [ ] 3. `cmd/slideforge-probe` + `pkg/slideforge/server`, build and deploy specs
- [ ] 4. Python control server (SlideForge repo)
- [ ] 5. Studio plugin (agy gemini-4-neon-medium; VM tests by me)
- [ ] 6. Documentation
- [ ] 7. Probe deployed on tp2

## Log
### 2026-11-24
- Plan written alongside SlideForge v1. The bot core, bridge and tools are in progress in the SlideForge repo.
- tp2 avahi was fixed first, so `dani-tp2.local` resolves for mDNS discovery. See `Devices/tp2/configurations/avahi-mdns.md`.
