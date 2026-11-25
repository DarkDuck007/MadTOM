# Progress

- [x] 1 Broadcasts tab (SfBroadcastsViewModel/TabView, 14 tests)
- [x] 2 File preview: office types go through get_job_file preview (needs SlideForge Python change, see below); text-like files as text; unknown types try text read
- [x] 3 Queue graph timeframe (1m..24h, tiered client-side history)
- [x] 4 Overview GridSplitter, Top users scroll
- [x] 5 Background refresh on tab open (Users, Guides, Config, Sandbox, Broadcasts)
- [x] 6 Sandbox in-place refresh, no overlap, 10 s throttle
- [x] 7 Sandbox bars no longer overlap text
- [x] 8 API & Tool calls: root cause was in SlideForge Python (emit_api_call never called); fixed in Python, pending deploy
- [x] 9 Job spec JSON Unicode (relaxed JSON encoder)
- [x] 10 Prompt preview: valid template, load spec from job, hint

## Log
- Python (SlideForge submodule, uncommitted): src/control/server.py, src/control/files.py, src/control/rpc.py, src/jobs/service.py
- Plugin: 94 tests pass, 0 warnings
