# Implementation plans

Every implementation plan lives in a dated folder: `plans/YYYY-MM-DD-<slug>/`.

Each folder holds:

| File | Purpose |
|---|---|
| `README.md` | Context, decisions, architecture and phasing: the plan itself. |
| `progress.md` | A phase checklist plus a dated work log, updated as work lands. Delegated jobs and review results are recorded here. |
| `contracts.md` | Interfaces between components (APIs, wire protocols, config keys, DB tables), when the plan has them. |
| other `*.md` | Per-phase or per-topic notes (findings, test plans, decisions made mid-way). |

Start a plan's folder on the day the plan is approved. Later changes to it go into `progress.md` as dated entries, and `README.md` is edited in place. The older `planning/` folder holds plans written before this convention.

## Index

| Date | Plan | Status |
|---|---|---|
| 2026-11-25 | [SlideForge plugin fixes: broadcasts, previews, graphs, layout](2026-11-25-slideforge-plugin-fixes/README.md) | in progress |
| 2026-11-24 | [SlideForge monitoring: Go probe, shared auth hardening, Studio plugin](2026-11-24-slideforge-plugin/README.md) | in progress |
| 2026-11-22 | ["Studio not working": rejected native tool calls; sandbox media tools](2026-11-22-native-tool-calls/README.md) | deployed |
| 2026-11-22 | [Skip audio for GIFs and silent videos; audio only when asked](2026-11-22-silent-media/README.md) | deployed (Studio UI pending) |
| 2026-11-20 | [Nox sandbox VMs, preprocessors, scheduler](2026-11-20-nox-sandbox/README.md) | in progress |
