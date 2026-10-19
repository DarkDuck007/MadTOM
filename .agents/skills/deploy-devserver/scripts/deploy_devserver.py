#!/usr/bin/env python3
"""
deploy_devserver.py

CLI helper for deploying updated MADTOM Studio and MADTOM-Collector binaries
to the active development test machine (default: lbb).

Usage:
    uv run scripts/deploy_devserver.py studio --output /tmp/studio_deploy.json
    uv run scripts/deploy_devserver.py collector --output /tmp/collector_deploy.json
    uv run scripts/deploy_devserver.py all --output /tmp/all_deploy.json
    uv run scripts/deploy_devserver.py status --output /tmp/status_report.json
"""

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path


def get_repo_root() -> Path:
    # Resolves repo root from this script location: <repo>/.agents/skills/deploy-devserver/scripts/
    script_path = Path(__file__).resolve()
    # Ascend to .agents, then repo root
    for parent in script_path.parents:
        if (parent / ".git").is_dir() or (parent / "publish.sh").is_file():
            return parent
    return script_path.parent.parent.parent.parent


def run_command(cmd: list[str], cwd: Path | None = None) -> tuple[int, str, str]:
    proc = subprocess.run(
        cmd,
        cwd=cwd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    return proc.returncode, proc.stdout.strip(), proc.stderr.strip()


def run_ssh(host: str, remote_cmd: str, user: str = "", port: int = 0) -> tuple[int, str, str]:
    ssh_target = f"{user}@{host}" if user else host
    ssh_args = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10"]
    if port and port > 0:
        ssh_args.extend(["-p", str(port)])
    ssh_args.extend([ssh_target, remote_cmd])
    return run_command(ssh_args)


def run_scp(local_path: Path, host: str, remote_path: str, user: str = "", port: int = 0) -> tuple[int, str, str]:
    scp_target = f"{user}@{host}:{remote_path}" if user else f"{host}:{remote_path}"
    scp_args = ["scp", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10"]
    if port and port > 0:
        scp_args.extend(["-P", str(port)])
    scp_args.extend([str(local_path), scp_target])
    return run_command(scp_args)


def deploy_studio(repo_root: Path, host: str, user: str, port: int) -> dict:
    result = {
        "component": "madtom-studio",
        "host": host,
        "status": "pending",
        "service": "madtom-studio.service",
    }

    print(f"==> [1/3] Building MADTOM Studio self-contained binary (linux-x64)...")
    publish_script = repo_root / "publish.sh"
    if not publish_script.is_file():
        result["status"] = "failed"
        result["error"] = f"publish.sh not found at {publish_script}"
        return result

    code, out, err = run_command([str(publish_script), "-p", "studio", "-a", "linux-x64"], cwd=repo_root)
    if code != 0:
        result["status"] = "failed"
        result["error"] = f"Studio build failed:\n{err or out}"
        return result

    local_bin = repo_root / "MADTOM_DOTNET" / "publish" / "linux-x64" / "MADTOM.Studio" / "MADTOM.Studio"
    if not local_bin.is_file():
        result["status"] = "failed"
        result["error"] = f"Expected compiled binary not found at {local_bin}"
        return result

    bin_size_mb = round(local_bin.stat().st_size / (1024 * 1024), 2)
    print(f"    Built successfully: {local_bin.name} ({bin_size_mb} MB)")

    remote_staging = f"/tmp/MADTOM.Studio_{os.getpid()}.new"
    print(f"==> [2/3] Uploading binary to {host}:{remote_staging}...")
    code, out, err = run_scp(local_bin, host, remote_staging, user=user, port=port)
    if code != 0:
        result["status"] = "failed"
        result["error"] = f"SCP upload failed: {err or out}"
        return result

    print(f"==> [3/3] Installing binary to /opt/madtom/MADTOM.Studio and restarting service...")
    remote_install_cmd = (
        f"sudo mkdir -p /opt/madtom && "
        f"sudo mv -f {remote_staging} /opt/madtom/MADTOM.Studio && "
        f"sudo chmod 755 /opt/madtom/MADTOM.Studio && "
        f"sudo chown danial:danial /opt/madtom/MADTOM.Studio 2>/dev/null || true && "
        f"sudo systemctl daemon-reload && "
        f"sudo systemctl restart madtom-studio.service"
    )
    code, out, err = run_ssh(host, remote_install_cmd, user=user, port=port)
    if code != 0:
        result["status"] = "failed"
        result["error"] = f"Remote installation/restart command failed:\n{err or out}"
        return result

    code, out, err = run_ssh(host, "sudo systemctl is-active madtom-studio.service", user=user, port=port)
    if code != 0 or out != "active":
        _, journal_out, _ = run_ssh(host, "sudo journalctl -u madtom-studio.service -n 25 --no-pager", user=user, port=port)
        result["status"] = "failed"
        result["error"] = f"Service is not active (state: {out}). Journal:\n{journal_out}"
        return result

    result["status"] = "success"
    result["details"] = "Service active (running)"
    print(f"    Status: madtom-studio.service is ACTIVE and running on {host}.")
    return result


def deploy_collector(repo_root: Path, host: str, user: str, port: int) -> dict:
    result = {
        "component": "madtom-collector",
        "host": host,
        "status": "pending",
        "service": "madtom-collector.service",
    }

    print(f"==> [1/2] Building and deploying MADTOM-Collector to {host}...")
    deploy_script = repo_root / "MADTOM_GOLANG" / "deploy.sh"
    if not deploy_script.is_file():
        result["status"] = "failed"
        result["error"] = f"deploy.sh not found at {deploy_script}"
        return result

    target_spec = f"{user}@{host}" if user else host
    deploy_args = [str(deploy_script), target_spec, "-P", "madtom-collector", "--build"]
    if port and port > 0:
        deploy_args.extend(["-p", str(port)])

    code, out, err = run_command(deploy_args, cwd=repo_root / "MADTOM_GOLANG")
    if code != 0:
        result["status"] = "failed"
        result["error"] = f"Collector deploy script failed:\n{err or out}"
        return result

    print(f"==> [2/2] Verifying madtom-collector service health on {host}...")
    code, out, err = run_ssh(host, "sudo systemctl is-active madtom-collector.service", user=user, port=port)
    if code != 0 or out != "active":
        _, journal_out, _ = run_ssh(host, "sudo journalctl -u madtom-collector.service -n 25 --no-pager", user=user, port=port)
        result["status"] = "failed"
        result["error"] = f"Service is not active (state: {out}). Journal:\n{journal_out}"
        return result

    result["status"] = "success"
    result["details"] = "Service active (running)"
    print(f"    Status: madtom-collector.service is ACTIVE and running on {host}.")
    return result


def query_status(host: str, user: str, port: int) -> dict:
    services = ["madtom-studio.service", "madtom-collector.service"]
    status_report = {"host": host, "services": {}}

    for svc in services:
        code, out, _ = run_ssh(host, f"systemctl is-active {svc}", user=user, port=port)
        state = out if code == 0 else (out or "inactive")
        _, journal, _ = run_ssh(host, f"journalctl -u {svc} -n 5 --no-pager", user=user, port=port)
        status_report["services"][svc] = {
            "state": state,
            "recent_logs": journal.splitlines() if journal else [],
        }
        print(f"  [{svc}] -> {state.upper()}")

    return status_report


def resolve_target_host(host: str, user: str, port: int) -> str:
    if host in ("lbw", "wg-lbw"):
        code, _, _ = run_ssh("lbw", "echo ok", user=user, port=port)
        if code == 0:
            return "lbw"
        print("    Notice: 'lbw' unreachable, falling back to 'wg-lbw'...")
        return "wg-lbw"
    return host


def main():
    parser = argparse.ArgumentParser(description="Deploy MADTOM Studio and/or Collector to devserver")
    subparsers = parser.add_subparsers(dest="subcommand", required=True)

    def add_common_args(p):
        p.add_argument(
            "--host",
            default=os.environ.get("DEVSERVER_HOST", "lbw"),
            help="Target devserver host (default: env DEVSERVER_HOST or 'lbw')",
        )
        p.add_argument("--user", default="", help="SSH username (default: SSH config)")
        p.add_argument("--port", type=int, default=0, help="SSH port (default: SSH config)")
        p.add_argument(
            "--output",
            required=True,
            help="Output JSON file path for results report",
        )

    # Subcommands
    sub_studio = subparsers.add_parser("studio", help="Deploy MADTOM Studio")
    add_common_args(sub_studio)

    sub_collector = subparsers.add_parser("collector", help="Deploy MADTOM Collector")
    add_common_args(sub_collector)

    sub_all = subparsers.add_parser("all", help="Deploy both Studio and Collector")
    add_common_args(sub_all)

    sub_status = subparsers.add_parser("status", help="Check status of services on devserver")
    add_common_args(sub_status)

    args = parser.parse_args()
    args.host = resolve_target_host(args.host, args.user, args.port)
    repo_root = get_repo_root()
    output_path = Path(args.output).resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)

    report = {"action": args.subcommand, "host": args.host, "results": []}
    overall_success = True

    try:
        if args.subcommand == "studio":
            res = deploy_studio(repo_root, args.host, args.user, args.port)
            report["results"].append(res)
            if res.get("status") != "success":
                overall_success = False

        elif args.subcommand == "collector":
            res = deploy_collector(repo_root, args.host, args.user, args.port)
            report["results"].append(res)
            if res.get("status") != "success":
                overall_success = False

        elif args.subcommand == "all":
            res_studio = deploy_studio(repo_root, args.host, args.user, args.port)
            report["results"].append(res_studio)
            if res_studio.get("status") != "success":
                overall_success = False

            res_collector = deploy_collector(repo_root, args.host, args.user, args.port)
            report["results"].append(res_collector)
            if res_collector.get("status") != "success":
                overall_success = False

        elif args.subcommand == "status":
            st = query_status(args.host, args.user, args.port)
            report["status_report"] = st

    except Exception as ex:
        overall_success = False
        report["exception"] = str(ex)
        print(f"Error during execution: {ex}", file=sys.stderr)

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)

    if overall_success:
        print(f"\nDeployment operation completed successfully! Report saved to: {output_path}")
        sys.exit(0)
    else:
        print(f"\nDeployment operation FAILED! Report saved to: {output_path}", file=sys.stderr)
        for res in report.get("results", []):
            if res.get("status") == "failed":
                print(f"  [{res.get('component')}] Error: {res.get('error')}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
