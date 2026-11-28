# Progress

- [x] NoxChat: schema, `src/moderation/bans.py`, config, docs
- [x] NoxChat: trigger / preprocess / commands / guest gates + automated responses
- [x] NoxChat: ignore_user scope + DM bans, /bans, /unban, prompt tag + tip, persona text
- [x] NoxChat: tests (`tests/test_abuse_bans.py`, 15), full suite 858 passed, 2 skipped
- [x] Probe: proto `ListBans` / `BanAction`, Go server forwarding, `bans_test.go`
- [x] Studio: Banned users section in User Quotas (delegate: agy gemini-4-neon-medium), parser `[banned]` badge, `BannedUsersTests.cs` (13); NoxAI tests 197/197
- [ ] Push, deploy NoxChat + probe to tp1

## Log
- 2026-11-28: built and tested end to end. Studio view placed inside User Quotas (no new top-level tab) to stay out of in-progress MainViewModel changes.
