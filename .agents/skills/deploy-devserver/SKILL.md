---
name: deploy-devserver
description: >-
  Builds and deploys updated MADTOM Studio (.NET Avalonia binary) and/or MADTOM-Collector (Go TSDB binary) to the designated development test machine (default: lbw, fallback wg-lbw), restarts systemd services, and verifies running health.
---

# Deploy Devserver

## Overview
Automates compiling, synchronizing, and restarting MADTOM Studio (`madtom-studio.service`) and MADTOM-Collector (`madtom-collector.service`) on the test server (default host: `lbw`, fallback: `wg-lbw`, or configured via `DEVSERVER_HOST`).

Use this skill whenever updates to `MADTOM_DOTNET` (Studio, Views, ViewModels, Themes) or `MADTOM_GOLANG` (Collector, TSDB, reverse-push ingestion) need to be tested immediately on the dev machine.

## Dependencies
- **`device-workflows`**: Adheres to the hardware tracking and configuration conventions stored under `Devices/<device_id>/` (e.g. `Devices/lbb/`).
- **`uv`**: Used to execute the Python CLI helper script `scripts/deploy_devserver.py`.

## Quick Start
Deploy MADTOM Studio to the dev machine:
```bash
uv run .agents/skills/deploy-devserver/scripts/deploy_devserver.py studio --output /tmp/studio_deploy.json
```

Deploy MADTOM-Collector to the dev machine:
```bash
uv run .agents/skills/deploy-devserver/scripts/deploy_devserver.py collector --output /tmp/collector_deploy.json
```

Deploy both Studio and Collector together:
```bash
uv run .agents/skills/deploy-devserver/scripts/deploy_devserver.py all --output /tmp/all_deploy.json
```

Check running status of both services on the dev machine:
```bash
uv run .agents/skills/deploy-devserver/scripts/deploy_devserver.py status --output /tmp/status_report.json
```

## Utility Scripts

The companion CLI helper is located at `scripts/deploy_devserver.py`.

### Subcommands
1. **`studio`**:
   - Compiles self-contained .NET binary: `./publish.sh -p studio -a linux-x64`
   - Uploads binary to `/tmp/MADTOM.Studio_<pid>.new` via SCP
   - Installs to `/opt/madtom/MADTOM.Studio` (`chmod 755`, `chown danial:danial`)
   - Restarts `madtom-studio.service` and verifies `is-active`
   - Accepts `--host` (default: `lbb`), `--user`, `--port`, `--output` (required)

2. **`collector`**:
   - Executes `./MADTOM_GOLANG/deploy.sh <host> -P madtom-collector`
   - Compiles Go binary, installs to `/opt/madtom-collector/bin/madtom-collector`
   - Restarts `madtom-collector.service` and verifies `is-active`
   - Accepts `--host`, `--user`, `--port`, `--output` (required)

3. **`all`**:
   - Runs `studio` deployment followed by `collector` deployment sequentially.

4. **`status`**:
   - Probes systemd status and captures the last 5 lines of journal logs for each service on the target machine.

## Workflow

### 1. Identify Touched Projects
- If changes were made to Avalonia UI, views, viewmodels, plugins, or host: trigger `studio`.
- If changes were made to collector TSDB, proto definitions, twamp, or ingestion: trigger `collector`.
- If both were touched: trigger `all`.

### 2. Execute Deployment
Run the corresponding subcommand using `uv run` and supply a report path:
```bash
uv run .agents/skills/deploy-devserver/scripts/deploy_devserver.py <subcommand> --output <output_path>
```

### 3. Handle Results
- If the script succeeds, confirm active service status in the report.
- If the script exits with non-zero status, read the output file to inspect the failure logs and report them directly without retrying blindly.

## Common Mistakes
1. **Omitting `--output`**: The script requires `--output <file>` to ensure structured logs and token-efficient reporting.
2. **Confusing Collector and Daemon**: MADTOM-Collector is deployed to `madtom-collector.service` (listening on gRPC :50051), while MADTOM-Daemon runs `madtomd.service` on client nodes (port 50052). Do not deploy daemon to `madtom-collector.service`.
3. **Hardcoding `lbb`**: While `lbb` is the default test machine, use `--host <name>` or `export DEVSERVER_HOST=<name>` if the dev machine alias changes.
