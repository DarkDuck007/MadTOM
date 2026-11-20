# Progress: preprocessors and crons

## Checklist
- [x] Plan and contracts (decision: crons run preprocessors only; static preprocessors for say/invoke reminders)
- [x] Go: `PreRun` state precedence fix. The state returned in the result object was being overwritten by the echoed input. Regression test `TestPreRunStateRoundTrip`.
- [x] Proto: 7 automation RPCs plus messages (written by me), Go code regenerated
- [x] NoxChat Python (codex gpt-6.2-sol, 19 min): config, schema, src/preprocess, src/cron, hooks, tools, guides, commands, RPCs
- [x] Go probe pass-throughs (codex gpt-6.1-sol, 102 s, clean). Wire tests `pkg/noxchat/server/automation_test.go` (mine) pass.
- [x] Studio Automation tab (agy gemini-4-argon-medium, 3.5 min; first launch failed: model id needs the `-medium` suffix). Indices shifted correctly.
- [x] My tests: pytest `test_preprocess_cron.py` 46 pass; Go `automation_test.go` pass; C# `AutomationTabTests.cs` 9 pass
- [x] Review all diffs (fixes below)
- [ ] Deploy to tp1 (cron.default_timezone), live E2E in chat 1998313581

## Log
- 2026-11-20:
  - Pre VM live check on tp1: about 140 ms exec, 250–430 ms round trip, busy loop → timeout, network blocked.
  - Found the state bug above.
- Review fixes (mine):
  - **ReDoS:** `filter.text_regex` ran with `re` on the event loop for every message. It now uses `regex` with `preprocess.filter_regex_timeout_ms` (default 20; a timeout counts as no match). The 300-character pattern cap is now `preprocess.max_filter_regex_chars`. `regex` is pinned in requirements.txt. Test `test_catastrophic_regex_cannot_stall_the_bot`.
  - **Base prompt:** `_base.yaml` now points to `tool_guide('cron')` and `tool_guide('preprocessors')`, and the guide test topic set is updated.
- Known and accepted:
  - a rejected `cron` action (for example the nested rule) counts as a run error toward auto-disable and skips that run's other actions;
  - a one-shot cron missed by more than `misfire_grace_seconds` during downtime is skipped (loophole 9).
- Not yet done: commit, tp1 deploy (nox-sandbox `PreRun` fix binary, NoxChat pull, `croniter` install, `cron.default_timezone`), live E2E.
