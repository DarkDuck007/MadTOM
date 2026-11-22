# Progress

- [x] Root cause from agy transcripts (incident 2026-11-22 18:42 JST, chat -1003206381698, session a6fdbd73)
- [x] Bridge recovery + reminder, tests (bridge 57 passed; fake ACP agent writes transcripts)
- [x] Sandbox base image media packages + build gate
- [x] Export list in config.yaml (verified the Go daemon applies it: 67 exts)
- [x] FILE- media sent playable, `kind` on `<document>`, tests (NoxChat 674 passed, 1 skipped)
- [x] Deployed NoxChat 7a4203c on tp1 (tests passed there first); bridge + noxchat restarted with no jobs active, nox-sandbox reloaded
- [x] Image rebuild with media packages: 2026.11.22-1 failed the build gate (pydub needs audioop, gone in Python 3.13), fixed with audioop-lts (NoxChat 721bac0); 2026.11.22-2 is current. E2E in a temp VM: ffmpeg + Pillow/raqm Persian caption → mp4 → gif → gifsicle, mp4 and gif exported.
- [ ] Watch bridge logs for "Recovered native call" / "returning … natively-called tool(s)" in real chats

## Log

- 2026-11-22: ACP probe on tp1 showed agy emits no ACP event for a rejected native call (only a usage_update), so
  the transcript is the only source. The model would not reproduce the native call on demand in 3 more probes; the
  real incidents were analysed from transcripts instead (13 of 14 directly after a tool result).
- Side finding (not changed): NetworkManager on tp1 rewrites resolv.conf every ~3 s.
