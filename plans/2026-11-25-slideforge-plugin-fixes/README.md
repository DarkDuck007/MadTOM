# SlideForge plugin fixes (2026-11-25)

Ten tasks on the MADTOM Studio SlideForge plugin:

1. Broadcasts tab (list, compose, audience count, test send, details, cancel)
2. File preview for non-image formats (page images / text)
3. Active-queue graph timeframe selector
4. Overview: resizable graph/stat area, scrollable Top users
5. Background refresh on tab open (Users, Guides, Config, ...)
6. Sandbox tab: stop flicker / partial refresh
7. Usage bars no longer overlap text
8. Raw Logs & API: "API & Tool calls" always empty
9. Job spec JSON: decoded Unicode
10. Prompt preview tab: fix bad_request, explain purpose

Rules: UI work via delegate (agy, gemini-4-neon-medium); no remote access/deploys; Python/Go changes listed in the final report.
