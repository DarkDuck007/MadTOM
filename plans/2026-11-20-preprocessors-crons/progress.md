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
- [x] Deploy to tp1 (cron.default_timezone Asia/Tokyo, sandbox.pre_vm.pool_size 4), live E2E in chat 1998313581

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
- Deploy: NoxChat `64e4e02` and `565d8b1`, MADTOM `1af64e8` and `514ccc0`. New nox-sandbox and noxchat-probe binaries on tp1. `croniter` and `regex` installed. Config backup: `config.yaml.bak-pre-automation`.
- Live E2E #1 found a real issue. The host wrapped the whole Pre VM call in `exec_timeout_ms` (300 ms), but the measured round trip is about 260 ms and 1–5 s while the 2-VM pool refills. So runs timed out and counted toward auto-disable. Fix: `preprocess.host_timeout_ms` (1500). Host timeouts and sandbox transport errors are now "skipped" and don't count as errors. Test `test_slow_pool_or_down_sandbox_skips_without_counting_errors`. `pool_size` on tp1 raised to 4.
- Live E2E #1 also showed, by accident, the 6th-consecutive rule in the real runtime. The static reminder fired on time while the streak was 5, so it was blocked and handed to Nox. Nox took a turn (LLM call) and the streak in the DB went back to 0.
- Live E2E #2, all pass:
  - 5 Pre VM pongs delivered through dispatch_stream, then the 6th became an invoke note;
  - state persisted (n=6);
  - drop suppressed for a regular user but not for the immune owner;
  - nested cron at 1 min rejected, at 6 min accepted (depth 1);
  - static reminder fired by the running service at 07:00:07Z and was delivered 0.4 s later.
- Cleanup: e2e preprocessors and crons deleted, streak reset, sandbox grant on 1998313581 revoked.
