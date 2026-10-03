---
name: device-workflows
description: Enforces standardized, step-by-step logging and procedure documentation for hardware device administration, deployments, and investigations under Devices/<device_id>/. Ensures in-content timestamps, command history, failure logs, and concise technical summaries.
---

# Device Administration & Workflow Documentation (`device-workflows`)

This skill defines the directory taxonomy, formatting conventions, and logging discipline for all operations, deployments, and investigative procedures executed across fleet and edge devices (e.g., `Devices/lbb/`).

---

## 1. Directory Structure

Each device tracked in the project root must follow a categorized directory layout:

```
Devices/<device_id>/
├── configurations/      # System services, daemon configs, display stack, deployed app setups
├── investigations/      # Hardware probes, kernel/driver behavior, thermal/fan diagnosis, benchmark logs
└── scripts/             # Local automation or helper scripts specifically tied to the device
```

> [!IMPORTANT]
> The top-level `Devices/` directory is git-ignored to prevent machine-specific logs and ephemeral hardware details from polluting the main repository. Do not force-commit `Devices/` unless explicitly instructed.

---

## 2. File Naming & Content Rules

### File Naming Conventions
- **Clean and descriptive**: Name files by function or topic (e.g. `madtom-studio.md`, `fan-control.md`, `twamp-network.md`).
- **NO timestamps in file names**: Avoid `2026-10-02-madtom-studio.md`. Dates belong inside the document content.
- Use lowercase alphanumeric characters and hyphens (`kebab-case`).

### In-Content Documentation Standards
Every device document must include:
1. **Header Metadata**:
   - Target device ID, hostname, IP/domain.
   - Hardware model, processor, kernel version.
   - Initial problem statement or deployment objective.
2. **Dense, Step-by-Step Breakdown**:
   - Numbered chronological steps (`### Step 1: ...`, `### Step 2: ...`).
   - In-content local/ISO timestamps for each phase.
   - Exact CLI commands executed (copy-pasteable).
3. **Mandatory Failure & Error Logging**:
   - Every failed attempt, unexpected crash, or permission denial must be documented.
   - Record exact error messages or terminal output verbatim.
   - Explain the technical root cause (e.g., missing package, cgroup v2 restriction, kernel module mismatch).
   - Document the verified resolution.
4. **Current State & Verification**:
   - Current operational state of the subsystem or service.
   - Commands to verify healthy operation.

---

## 3. Standard Document Template

```markdown
# [Topic Name]: <device_id> (<hardware_model>)

- **Target Device**: `<device_id>` (`<hostname>`)
- **OS / Kernel**: <distro>, <kernel_version>
- **Component / Subsystem**: <subsystem>
- **Primary Objective**: <short summary>

---

## Chronological Steps & Action Log

### Step 1: <Action Title>
- **Timestamp**: YYYY-MM-DD HH:MM <TZ>
- **Command(s)**:
  ```bash
  <command>
  ```
- **Observations / Result**: <dense summary>

### Step 2: <Failure Title> (if applicable)
- **Timestamp**: YYYY-MM-DD HH:MM <TZ>
- **Command(s)**:
  ```bash
  <command>
  ```
- **Failure Output**:
  ```
  <verbatim error>
  ```
- **Root Cause**: <technical analysis>
- **Resolution**: <applied fix>

---

## Current Status & Verification
<status commands and output>
```
