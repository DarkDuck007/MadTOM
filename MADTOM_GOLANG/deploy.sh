#!/usr/bin/env bash
# ==============================================================================
# MADTOM Go Backend Deployment Script
# ==============================================================================
# Deploys MADTOM Go backend binaries (madtom-daemon, madtom-collector) to
# one or more remote Linux hosts via SSH/SCP, installs them using sudo, and
# restarts the specified systemd service.
#
# Supports:
#   - Single host deployment via CLI flags
#   - Multi-host deployment via YAML configuration file (-c / --config)
#   - SSH & Sudo password authentication via CLI flags (--ssh-pass, --sudo-pass)
#   - Remote systemd service override via CLI (-s / --service) and config file
#   - Automatic remote architecture detection and cross-compilation caching
# ==============================================================================

set -euo pipefail

# ------------------------------------------------------------------------------
# Defaults & Global Settings
# ------------------------------------------------------------------------------
DEFAULT_DEST_PATH="${DEST_PATH:-/opt/madtomd}"
DEFAULT_SERVICE_NAME="${SERVICE_NAME:-madtomd.service}"
DEFAULT_LOCAL_BIN="${LOCAL_BIN:-bin/madtom-daemon}"
DEFAULT_REMOTE_TMP_DIR="${REMOTE_TMP_DIR:-/tmp}"
DEFAULT_SSH_PORT="${SSH_PORT:-22}"

DEFAULT_SSH_PASS="${SSH_PASS:-${SSH_PASSWORD:-}}"
DEFAULT_SUDO_PASS="${SUDO_PASS:-${SUDO_PASSWORD:-}}"

HOST_UNAME="$(uname -m)"
case "$HOST_UNAME" in
    x86_64)               DEFAULT_ARCH="amd64" ;;
    aarch64|arm64|armv8*) DEFAULT_ARCH="arm64" ;;
    armv7*|armv6*|armhf)  DEFAULT_ARCH="arm" ;;
    *)                    DEFAULT_ARCH="amd64" ;;
esac

# Resolve script directory (physical path in case of symlink) and repo root
SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Global cleanup for temporary credentials
CURRENT_ASKPASS_FILE=""

cleanup() {
    if [ -n "${CURRENT_ASKPASS_FILE:-}" ] && [ -f "$CURRENT_ASKPASS_FILE" ]; then
        rm -f "$CURRENT_ASKPASS_FILE"
    fi
    unset MADTOM_DEPLOY_PASS 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# Track built targets during current execution to avoid redundant recompilation
# Format: " bin_name:arch bin_name:arch "
BUILT_TARGETS=" "

# ------------------------------------------------------------------------------
# Usage Information
# ------------------------------------------------------------------------------
print_usage() {
    cat << EOF
Usage: $(basename "$0") [OPTIONS] [USER@HOST | USER HOST]
       $(basename "$0") -c config.yaml [OPTIONS]

Deploy MADTOM Go backend executable to one or more remote systems via SSH.

Options:
  -c, --config PATH     Path to YAML configuration file for multi-host deployment
  -u, --user USER       SSH username
  -h, --host HOST       Remote hostname or IP address
  -p, --port PORT       SSH port (default: $DEFAULT_SSH_PORT)
  -b, --binary PATH     Local binary to deploy (default: $DEFAULT_LOCAL_BIN)
  -d, --dest PATH       Remote destination path (default: $DEFAULT_DEST_PATH)
  -s, --service NAME    Remote systemd service name (default: $DEFAULT_SERVICE_NAME)
  -a, --arch ARCH       Target architecture:
                          - amd64   (x86_64)
                          - arm64   (ARM 64-bit / aarch64)
                          - arm     (ARM 32-bit v7 / armhf)
                          - auto    (detect remote architecture via SSH, default)
  --ssh-pass PASS       SSH login password
  --sudo-pass PASS      Remote sudo password (defaults to --ssh-pass if omitted)
  --build               Build the Go binary locally before deploying
  --dry-run             Validate and print deployment targets without executing
  --help                Show this help message and exit

Environment Variables:
  SSH_PASS / SSH_PASSWORD    Default SSH password
  SUDO_PASS / SUDO_PASSWORD  Default remote sudo password
  CONFIG_FILE                Default config file path

Examples:
  # Deploy to multiple servers defined in deploy.yaml:
  $(basename "$0") -c deploy.yaml

  # Single host with password arguments:
  $(basename "$0") -u danial -h la.realiteam.art --ssh-pass secret123 --sudo-pass secret123

  # Single host with custom service name and auto-probe build:
  $(basename "$0") danial@oc1.realiteam.art -s madtom-custom.service --build

  # Override service name across all servers defined in deploy.yaml:
  $(basename "$0") -c deploy.yaml -s madtomd.service
EOF
}

# ------------------------------------------------------------------------------
# Argument Parsing
# ------------------------------------------------------------------------------
CONFIG_FILE="${CONFIG_FILE:-}"
CLI_USER=""
CLI_HOST=""
CLI_PORT=""
CLI_BINARY=""
CLI_DEST=""
CLI_SERVICE=""
CLI_ARCH=""
CLI_BUILD=0
CLI_SSH_PASS=""
CLI_SUDO_PASS=""
DRY_RUN=0

CLI_SERVICE_SET=0
CLI_BUILD_SET=0
CLI_BINARY_SET=0
CLI_DEST_SET=0
CLI_ARCH_SET=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--config)
            CONFIG_FILE="$2"
            shift 2
            ;;
        -u|--user)
            CLI_USER="$2"
            shift 2
            ;;
        -h|--host)
            CLI_HOST="$2"
            shift 2
            ;;
        -p|--port)
            CLI_PORT="$2"
            shift 2
            ;;
        -b|--binary)
            CLI_BINARY="$2"
            CLI_BINARY_SET=1
            shift 2
            ;;
        -d|--dest)
            CLI_DEST="$2"
            CLI_DEST_SET=1
            shift 2
            ;;
        -s|--service)
            CLI_SERVICE="$2"
            CLI_SERVICE_SET=1
            shift 2
            ;;
        -a|--arch)
            case "${2,,}" in
                amd64|x86_64|x64)          CLI_ARCH="amd64" ;;
                arm64|aarch64|linux-arm64) CLI_ARCH="arm64" ;;
                arm|armv7|armhf|linux-arm) CLI_ARCH="arm" ;;
                auto)                      CLI_ARCH="auto" ;;
                *)                         CLI_ARCH="$2" ;;
            esac
            CLI_ARCH_SET=1
            shift 2
            ;;
        --ssh-pass|--ssh-password|--password)
            CLI_SSH_PASS="$2"
            shift 2
            ;;
        --sudo-pass|--sudo-password)
            CLI_SUDO_PASS="$2"
            shift 2
            ;;
        --build)
            CLI_BUILD=1
            CLI_BUILD_SET=1
            shift
            ;;
        --dry-run)
            DRY_RUN=1
            shift
            ;;
        --help)
            print_usage
            exit 0
            ;;
        *@*)
            CLI_USER="${1%%@*}"
            CLI_HOST="${1##*@}"
            shift
            ;;
        *)
            if [ -z "$CLI_USER" ]; then
                CLI_USER="$1"
            elif [ -z "$CLI_HOST" ]; then
                CLI_HOST="$1"
            else
                echo "Error: Unknown argument: $1" >&2
                print_usage
                exit 1
            fi
            shift
            ;;
    esac
done

# If no config file was passed and no host specified, check for deploy.yaml in cwd or repo root
if [ -z "$CONFIG_FILE" ] && [ -z "$CLI_HOST" ]; then
    if [ -f "deploy.yaml" ]; then
        CONFIG_FILE="deploy.yaml"
        echo "Notice: Found 'deploy.yaml' in current directory, using it for deployment."
    elif [ -f "$REPO_ROOT/deploy.yaml" ]; then
        CONFIG_FILE="$REPO_ROOT/deploy.yaml"
        echo "Notice: Found '$REPO_ROOT/deploy.yaml', using it for deployment."
    elif [ -f "$REPO_ROOT/MADTOM_GOLANG/deploy.yaml" ]; then
        CONFIG_FILE="$REPO_ROOT/MADTOM_GOLANG/deploy.yaml"
        echo "Notice: Found '$REPO_ROOT/MADTOM_GOLANG/deploy.yaml', using it for deployment."
    elif [ -f "$REPO_ROOT/MADTOM_GOLANG/configs/telemetry/deploy.yaml" ]; then
        CONFIG_FILE="$REPO_ROOT/MADTOM_GOLANG/configs/telemetry/deploy.yaml"
        echo "Notice: Found '$REPO_ROOT/MADTOM_GOLANG/configs/telemetry/deploy.yaml', using it for deployment."
    elif [ -f "configs/telemetry/deploy.yaml" ]; then
        CONFIG_FILE="configs/telemetry/deploy.yaml"
        echo "Notice: Found 'configs/telemetry/deploy.yaml', using it for deployment."
    fi
fi

# ------------------------------------------------------------------------------
# Architecture Inspection Helper
# ------------------------------------------------------------------------------
get_binary_arch() {
    local bin_path="$1"
    if [ ! -f "$bin_path" ]; then
        echo "none"
        return
    fi
    local file_desc
    file_desc="$(file -b "$bin_path" 2>/dev/null || true)"
    if echo "$file_desc" | grep -qi "aarch64"; then
        echo "arm64"
    elif echo "$file_desc" | grep -qi "ARM"; then
        echo "arm"
    elif echo "$file_desc" | grep -qi "x86-64"; then
        echo "amd64"
    elif echo "$file_desc" | grep -qi "Intel 80386"; then
        echo "386"
    else
        echo "unknown"
    fi
}

# ------------------------------------------------------------------------------
# Binary Compilation Helper
# ------------------------------------------------------------------------------
build_target_binary() {
    local bin_path="$1"
    local target_arch="$2"
    local bin_name
    bin_name="$(basename "$bin_path")"

    # Locate Go source package
    local cmd_dir=""
    local go_src_dir="$SCRIPT_DIR"

    if [ "$bin_name" = "madtom-squeeze" ] || [ "$bin_name" = "squeeze-server" ]; then
        cmd_dir="./cmd/squeeze-server"
    elif [ -d "$SCRIPT_DIR/cmd/$bin_name" ]; then
        cmd_dir="./cmd/$bin_name"
    elif [ -d "$REPO_ROOT/MADTOM_GOLANG/cmd/$bin_name" ]; then
        go_src_dir="$REPO_ROOT/MADTOM_GOLANG"
        cmd_dir="./cmd/$bin_name"
    elif [ -d "$SCRIPT_DIR/cmd/madtom-daemon" ]; then
        cmd_dir="./cmd/madtom-daemon"
    elif [ -d "$REPO_ROOT/MADTOM_GOLANG/cmd/madtom-daemon" ]; then
        go_src_dir="$REPO_ROOT/MADTOM_GOLANG"
        cmd_dir="./cmd/madtom-daemon"
    fi

    if [ -z "$cmd_dir" ]; then
        echo "Error: Cannot find Go source to build $bin_name (checked cmd/$bin_name and cmd/madtom-daemon)." >&2
        return 1
    fi

    echo "==> Compiling Go binary '$bin_name' for linux/$target_arch..."
    mkdir -p "$(dirname "$bin_path")"
    mkdir -p "$SCRIPT_DIR/bin/linux_${target_arch}"

    local goarm_arg=()
    if [ "$target_arch" = "arm" ]; then
        goarm_arg=("GOARM=7")
    fi

    (cd "$go_src_dir" && env CGO_ENABLED=0 GOOS=linux GOARCH="$target_arch" "${goarm_arg[@]}" \
        go build -buildvcs=false -ldflags="-s -w" -o "$bin_path" "$cmd_dir")

    cp -f "$bin_path" "$SCRIPT_DIR/bin/linux_${target_arch}/$bin_name" 2>/dev/null || true
    echo "    Build complete: $bin_path (linux/$target_arch)"
    BUILT_TARGETS="${BUILT_TARGETS}${bin_name}:${target_arch} "
    return 0
}

# ------------------------------------------------------------------------------
# Single-Host Deployment Function
# ------------------------------------------------------------------------------
deploy_single_host() {
    local host="$1"
    local user="$2"
    local port="$3"
    local service="$4"
    local dest="$5"
    local local_bin="$6"
    local target_arch="$7"
    local do_build="$8"
    local ssh_pass="$9"
    local sudo_pass="${10}"

    if [ -z "$sudo_pass" ]; then
        sudo_pass="$ssh_pass"
    fi

    if [ -z "$user" ] || [ -z "$host" ]; then
        echo "Error: Host ($host) and User ($user) must be defined." >&2
        return 1
    fi

    # Interactive password prompt if missing and terminal attached
    if [ -z "$ssh_pass" ] && [ -t 0 ]; then
        read -r -s -p "Enter SSH password for ${user}@${host} (leave empty for SSH key auth): " ssh_pass
        echo ""
        if [ -n "$ssh_pass" ] && [ -z "$sudo_pass" ]; then
            read -r -s -p "Enter Sudo password (press Enter to use SSH password): " sudo_pass
            echo ""
            sudo_pass="${sudo_pass:-$ssh_pass}"
        fi
    fi

    # Prepare SSH authentication wrapper
    local use_sshpass=0

    if [ -n "$ssh_pass" ]; then
        if command -v sshpass >/dev/null 2>&1; then
            use_sshpass=1
        else
            CURRENT_ASKPASS_FILE="$(mktemp /tmp/madtom_askpass_XXXXXX.sh)"
            chmod 700 "$CURRENT_ASKPASS_FILE"
            cat << 'EOF' > "$CURRENT_ASKPASS_FILE"
#!/usr/bin/env bash
echo "$MADTOM_DEPLOY_PASS"
EOF
        fi
    fi

    run_ssh() {
        if [ "$use_sshpass" -eq 1 ]; then
            sshpass -p "$ssh_pass" ssh -p "$port" \
                -o StrictHostKeyChecking=accept-new \
                -o ConnectTimeout=15 \
                "${user}@${host}" "$@"
        elif [ -n "$CURRENT_ASKPASS_FILE" ]; then
            export MADTOM_DEPLOY_PASS="$ssh_pass"
            export SSH_ASKPASS="$CURRENT_ASKPASS_FILE"
            export SSH_ASKPASS_REQUIRE="force"
            export DISPLAY="${DISPLAY:-dummy:0}"
            ssh -p "$port" \
                -o StrictHostKeyChecking=accept-new \
                -o ConnectTimeout=15 \
                "${user}@${host}" "$@"
        else
            ssh -p "$port" \
                -o StrictHostKeyChecking=accept-new \
                -o ConnectTimeout=15 \
                "${user}@${host}" "$@"
        fi
    }

    run_scp() {
        local src="$1"
        local dst="$2"
        if [ "$use_sshpass" -eq 1 ]; then
            sshpass -p "$ssh_pass" scp -P "$port" \
                -o StrictHostKeyChecking=accept-new \
                -o ConnectTimeout=15 \
                "$src" "$dst"
        elif [ -n "$CURRENT_ASKPASS_FILE" ]; then
            export MADTOM_DEPLOY_PASS="$ssh_pass"
            export SSH_ASKPASS="$CURRENT_ASKPASS_FILE"
            export SSH_ASKPASS_REQUIRE="force"
            export DISPLAY="${DISPLAY:-dummy:0}"
            scp -P "$port" \
                -o StrictHostKeyChecking=accept-new \
                -o ConnectTimeout=15 \
                "$src" "$dst"
        else
            scp -P "$port" \
                -o StrictHostKeyChecking=accept-new \
                -o ConnectTimeout=15 \
                "$src" "$dst"
        fi
    }

    # Resolve local binary location
    if [ ! -f "$local_bin" ]; then
        if [ -f "$REPO_ROOT/$local_bin" ]; then
            local_bin="$REPO_ROOT/$local_bin"
        elif [ -f "$SCRIPT_DIR/$local_bin" ]; then
            local_bin="$SCRIPT_DIR/$local_bin"
        fi
    fi

    local bin_name
    bin_name="$(basename "$local_bin")"

    # Remote architecture detection
    if [ "$target_arch" = "auto" ]; then
        if [ "$DRY_RUN" -eq 0 ]; then
            echo "==> Probing remote architecture on ${user}@${host}:${port}..."
            local remote_machine
            remote_machine="$(run_ssh uname -m 2>/dev/null || true)"
            remote_machine="$(echo "$remote_machine" | tr -d '\r\n[:space:]')"
            if [ -n "$remote_machine" ]; then
                case "$remote_machine" in
                    x86_64|amd64)         target_arch="amd64" ;;
                    aarch64|arm64|armv8*) target_arch="arm64" ;;
                    armv7*|armv6*|armhf)  target_arch="arm" ;;
                    i386|i686)            target_arch="386" ;;
                    *)                    target_arch="$DEFAULT_ARCH" ;;
                esac
                echo "    Detected remote architecture: $remote_machine (mapped to linux/$target_arch)"
            else
                echo "    Notice: Could not detect architecture via SSH. Falling back to host default ($DEFAULT_ARCH)."
                target_arch="$DEFAULT_ARCH"
            fi
        else
            target_arch="$DEFAULT_ARCH"
        fi
    fi

    # Check if binary must be built or reused from cache
    local cached_arch_bin="$SCRIPT_DIR/bin/linux_${target_arch}/$bin_name"
    local bin_arch="unknown"
    if [ -f "$local_bin" ]; then
        bin_arch="$(get_binary_arch "$local_bin")"
    fi

    local need_build=0
    if [ "$do_build" -eq 1 ] || [ ! -f "$local_bin" ] || [ "$bin_arch" != "$target_arch" ]; then
        need_build=1
    fi

    if [ "$need_build" -eq 1 ]; then
        # Check if already compiled in this session for target_arch
        if [[ "$BUILT_TARGETS" == *" ${bin_name}:${target_arch} "* ]] && [ -f "$cached_arch_bin" ]; then
            echo "==> Reusing cached Go binary: $cached_arch_bin (linux/$target_arch)"
            mkdir -p "$(dirname "$local_bin")"
            cp -f "$cached_arch_bin" "$local_bin"
        else
            if [ "$DRY_RUN" -eq 0 ]; then
                build_target_binary "$local_bin" "$target_arch"
            else
                echo "[DRY RUN] Would compile Go binary for linux/$target_arch"
            fi
        fi
    fi

    # Destination path calculation
    local target_dir
    local target_bin
    if [ "$(basename "$dest")" != "$bin_name" ]; then
        target_dir="$dest"
        target_bin="$dest/$bin_name"
    else
        target_dir="$(dirname "$dest")"
        target_bin="$dest"
    fi

    local bin_size="N/A"
    if [ -f "$local_bin" ]; then
        bin_size="$(du -h "$local_bin" | cut -f1)"
    fi

    echo "----------------------------------------------------------------"
    echo "  Host         : ${user}@${host}:${port}"
    echo "  Architecture : linux/${target_arch}"
    echo "  Local Binary : $local_bin ($bin_size)"
    echo "  Destination  : $target_bin"
    echo "  Service Name : $service"
    echo "----------------------------------------------------------------"

    if [ "$DRY_RUN" -eq 1 ]; then
        echo "[DRY RUN] Validation succeeded for ${user}@${host} (no remote actions performed)."
        cleanup
        return 0
    fi

    # Step 1: Upload binary to temporary staging location
    local remote_tmp="${DEFAULT_REMOTE_TMP_DIR}/madtom_deploy_$(date +%s)_$RANDOM"
    echo "==> [1/3] Uploading binary to remote staging path: $remote_tmp ..."
    run_scp "$local_bin" "${user}@${host}:${remote_tmp}"

    # Step 2: Install via sudo & Step 3: Restart systemd service
    echo "==> [2/3] Installing binary to $target_bin with sudo ..."
    echo "==> [3/3] Restarting systemd service: $service ..."

    local remote_script
    remote_script=$(cat << EOF
set -euo pipefail

# Read password from stdin
IFS= read -r SUDO_PASS || true

run_sudo() {
    if [ -n "\$SUDO_PASS" ]; then
        printf '%s\n' "\$SUDO_PASS" | sudo -S -p '' "\$@"
    else
        sudo -n "\$@" 2>/dev/null || sudo "\$@"
    fi
}

TARGET_DIR="$target_dir"
TARGET_BIN="$target_bin"

# Clean up stale regular file if target dir exists as a file
if [ -f "\$TARGET_DIR" ]; then
    echo "    Cleaning up existing regular file at directory path: \$TARGET_DIR"
    run_sudo rm -f "\$TARGET_DIR"
fi

if [ ! -d "\$TARGET_DIR" ]; then
    echo "    Creating destination directory: \$TARGET_DIR"
    run_sudo mkdir -p "\$TARGET_DIR"
fi

echo "    Copying binary from $remote_tmp to \$TARGET_BIN (sudo)"
run_sudo cp -f "$remote_tmp" "\${TARGET_BIN}.new"
run_sudo chmod 755 "\${TARGET_BIN}.new"
run_sudo chown root:root "\${TARGET_BIN}.new" 2>/dev/null || true
run_sudo mv -f "\${TARGET_BIN}.new" "\$TARGET_BIN"

# Cleanup staging file
rm -f "$remote_tmp"

# Systemd operations
if command -v systemctl >/dev/null 2>&1; then
    SERVICE_EXEC="\$(run_sudo systemctl cat "$service" 2>/dev/null | grep -E '^\s*ExecStart=' | head -n1 | awk '{print \$1}' | sed 's/^\s*ExecStart=//' || true)"
    if [ -n "\$SERVICE_EXEC" ] && [ "\$SERVICE_EXEC" != "\$TARGET_BIN" ]; then
        SERVICE_EXEC_DIR="\$(dirname "\$SERVICE_EXEC")"
        if [ -f "\$SERVICE_EXEC_DIR" ]; then
            run_sudo rm -f "\$SERVICE_EXEC_DIR"
        fi
        if [ ! -d "\$SERVICE_EXEC_DIR" ]; then
            run_sudo mkdir -p "\$SERVICE_EXEC_DIR"
        fi
        echo "    Notice: Service specifies ExecStart=\$SERVICE_EXEC, syncing binary there"
        run_sudo cp -f "\$TARGET_BIN" "\${SERVICE_EXEC}.new"
        run_sudo chmod 755 "\${SERVICE_EXEC}.new"
        run_sudo chown root:root "\${SERVICE_EXEC}.new" 2>/dev/null || true
        run_sudo mv -f "\${SERVICE_EXEC}.new" "\$SERVICE_EXEC"
    fi

    run_sudo systemctl daemon-reload 2>/dev/null || true
    echo "    Restarting service: $service"
    run_sudo systemctl restart "$service"

    echo "    Verifying service status..."
    if run_sudo systemctl is-active --quiet "$service"; then
        echo "    Status: ACTIVE (Running)"
    else
        echo "    Warning: Service '$service' is not active. Recent service journal:"
        run_sudo journalctl -u "$service" -n 20 --no-pager || true
        exit 1
    fi
else
    echo "    Notice: systemctl not detected on remote host. Binary updated successfully."
fi
EOF
)

    # Execute remote installer piping sudo password through stdin
    printf '%s\n' "$sudo_pass" | run_ssh "bash -c $(printf %q "$remote_script")"

    cleanup
    echo "==> Deployment to ${user}@${host} completed successfully!"
    return 0
}

# ------------------------------------------------------------------------------
# Execution Orchestration (Multi-Host Config or Single Host)
# ------------------------------------------------------------------------------

DEPLOY_SUCCESSES=()
DEPLOY_FAILURES=()

if [ -n "$CONFIG_FILE" ]; then
    if [ ! -f "$CONFIG_FILE" ]; then
        echo "Error: Configuration file '$CONFIG_FILE' not found." >&2
        exit 1
    fi

    echo "================================================================"
    echo " MADTOM Deployment - Config Mode: $CONFIG_FILE"
    echo "================================================================"

    # Parse YAML using python3 PyYAML
    SERVERS_JSON_LINES="$(python3 - "$CONFIG_FILE" << 'PYEOF'
import sys, json

try:
    import yaml
except ImportError:
    print("Error: Python 'pyyaml' is required to parse YAML configs. Install with: pip install pyyaml", file=sys.stderr)
    sys.exit(1)

config_path = sys.argv[1]
try:
    with open(config_path, 'r', encoding='utf-8') as f:
        data = yaml.safe_load(f) or {}
except Exception as e:
    print(f"Error loading YAML '{config_path}': {e}", file=sys.stderr)
    sys.exit(1)

defaults = data.get('defaults', {}) or {}
raw_servers = data.get('servers') or data.get('hosts') or []

if not raw_servers:
    print(f"Error: No servers listed in '{config_path}'.", file=sys.stderr)
    sys.exit(1)

for s in raw_servers:
    if isinstance(s, str):
        h = s.strip()
        u = defaults.get('user', '')
        p = defaults.get('port', 22)
        if '@' in h:
            u, h = h.split('@', 1)
        if ':' in h:
            h, p_str = h.split(':', 1)
            try:
                p = int(p_str)
            except ValueError:
                pass
        s = {'host': h, 'user': u, 'port': p}

    if not isinstance(s, dict):
        continue

    host = str(s.get('host', '')).strip()
    if not host:
        continue

    user = str(s.get('user') or defaults.get('user', '')).strip()
    port = s.get('port') or defaults.get('port', 22)
    service = str(s.get('service') or defaults.get('service', 'madtomd.service')).strip()
    dest = str(s.get('dest') or defaults.get('dest', '/opt/madtomd')).strip()
    binary = str(s.get('binary') or defaults.get('binary', 'bin/madtom-daemon')).strip()
    arch = str(s.get('arch') or defaults.get('arch', 'auto')).strip()
    build = s.get('build', defaults.get('build', False))
    if isinstance(build, str):
        build = build.lower() in ('true', '1', 'yes')

    ssh_pass = s.get('ssh_pass') or s.get('ssh_password') or s.get('password') or defaults.get('ssh_pass') or defaults.get('ssh_password') or defaults.get('password') or ''
    sudo_pass = s.get('sudo_pass') or s.get('sudo_password') or defaults.get('sudo_pass') or defaults.get('sudo_password') or ssh_pass

    entry = {
        'host': host,
        'user': user,
        'port': int(port),
        'service': service,
        'dest': dest,
        'binary': binary,
        'arch': arch,
        'build': bool(build),
        'ssh_pass': str(ssh_pass),
        'sudo_pass': str(sudo_pass)
    }
    print(json.dumps(entry))
PYEOF
)"

    # Read server targets into array
    mapfile -t SERVERS_ARRAY <<< "$SERVERS_JSON_LINES"
    TOTAL_SERVERS="${#SERVERS_ARRAY[@]}"

    if [ "$TOTAL_SERVERS" -eq 0 ] || [ -z "${SERVERS_ARRAY[0]}" ]; then
        echo "Error: No valid servers parsed from '$CONFIG_FILE'." >&2
        exit 1
    fi

    CURRENT_IDX=0
    for server_json in "${SERVERS_ARRAY[@]}"; do
        [ -z "$server_json" ] && continue
        CURRENT_IDX=$((CURRENT_IDX + 1))

        # Safely evaluate fields using python shlex
        eval "$(python3 - "$server_json" << 'PYEOF'
import sys, json, shlex
data = json.loads(sys.argv[1])
for k, v in data.items():
    print(f"S_{k.upper()}={shlex.quote(str(v))}")
PYEOF
)"

        # Apply CLI overrides if passed
        TARGET_HOST="$S_HOST"
        TARGET_USER="${CLI_USER:-$S_USER}"
        TARGET_PORT="${CLI_PORT:-$S_PORT}"
        TARGET_SERVICE="$S_SERVICE"
        if [ "$CLI_SERVICE_SET" -eq 1 ]; then
            TARGET_SERVICE="$CLI_SERVICE"
        fi
        TARGET_DEST="$S_DEST"
        if [ "$CLI_DEST_SET" -eq 1 ]; then
            TARGET_DEST="$CLI_DEST"
        fi
        TARGET_BIN="$S_BINARY"
        if [ "$CLI_BINARY_SET" -eq 1 ]; then
            TARGET_BIN="$CLI_BINARY"
        fi
        TARGET_ARCH="$S_ARCH"
        if [ "$CLI_ARCH_SET" -eq 1 ]; then
            TARGET_ARCH="$CLI_ARCH"
        fi
        TARGET_BUILD=0
        if [ "$S_BUILD" = "True" ] || [ "$CLI_BUILD_SET" -eq 1 ]; then
            TARGET_BUILD=1
        fi
        TARGET_SSH_PASS="${CLI_SSH_PASS:-${DEFAULT_SSH_PASS:-$S_SSH_PASS}}"
        TARGET_SUDO_PASS="${CLI_SUDO_PASS:-${DEFAULT_SUDO_PASS:-$S_SUDO_PASS}}"

        echo ""
        echo "================================================================"
        echo " [$CURRENT_IDX/$TOTAL_SERVERS] Host: ${TARGET_USER}@${TARGET_HOST}:${TARGET_PORT}"
        echo "================================================================"

        if deploy_single_host "$TARGET_HOST" "$TARGET_USER" "$TARGET_PORT" \
                              "$TARGET_SERVICE" "$TARGET_DEST" "$TARGET_BIN" \
                              "$TARGET_ARCH" "$TARGET_BUILD" \
                              "$TARGET_SSH_PASS" "$TARGET_SUDO_PASS"; then
            DEPLOY_SUCCESSES+=("${TARGET_USER}@${TARGET_HOST} (service: ${TARGET_SERVICE})")
        else
            DEPLOY_FAILURES+=("${TARGET_USER}@${TARGET_HOST} (service: ${TARGET_SERVICE})")
        fi
    done

else
    # Single-host mode
    TARGET_HOST="$CLI_HOST"
    TARGET_USER="$CLI_USER"
    TARGET_PORT="${CLI_PORT:-$DEFAULT_SSH_PORT}"
    TARGET_SERVICE="${CLI_SERVICE:-$DEFAULT_SERVICE_NAME}"
    TARGET_DEST="${CLI_DEST:-$DEFAULT_DEST_PATH}"
    TARGET_BIN="${CLI_BINARY:-$DEFAULT_LOCAL_BIN}"
    TARGET_ARCH="${CLI_ARCH:-$DEFAULT_ARCH}"
    TARGET_BUILD="$CLI_BUILD"
    TARGET_SSH_PASS="${CLI_SSH_PASS:-$DEFAULT_SSH_PASS}"
    TARGET_SUDO_PASS="${CLI_SUDO_PASS:-${DEFAULT_SUDO_PASS:-$TARGET_SSH_PASS}}"

    if [ -z "$TARGET_USER" ] && [ -t 0 ]; then
        read -r -p "Enter remote SSH username: " TARGET_USER
    fi
    if [ -z "$TARGET_HOST" ] && [ -t 0 ]; then
        read -r -p "Enter remote host / IP: " TARGET_HOST
    fi

    if [ -z "$TARGET_USER" ] || [ -z "$TARGET_HOST" ]; then
        echo "Error: Both SSH user and host are required." >&2
        print_usage
        exit 1
    fi

    echo "================================================================"
    echo " MADTOM Deployment - Single Host: ${TARGET_USER}@${TARGET_HOST}"
    echo "================================================================"

    if deploy_single_host "$TARGET_HOST" "$TARGET_USER" "$TARGET_PORT" \
                          "$TARGET_SERVICE" "$TARGET_DEST" "$TARGET_BIN" \
                          "$TARGET_ARCH" "$TARGET_BUILD" \
                          "$TARGET_SSH_PASS" "$TARGET_SUDO_PASS"; then
        DEPLOY_SUCCESSES+=("${TARGET_USER}@${TARGET_HOST} (service: ${TARGET_SERVICE})")
    else
        DEPLOY_FAILURES+=("${TARGET_USER}@${TARGET_HOST} (service: ${TARGET_SERVICE})")
    fi
fi

# ------------------------------------------------------------------------------
# Deployment Summary Report
# ------------------------------------------------------------------------------
TOTAL_DEPLOYED=$(( ${#DEPLOY_SUCCESSES[@]} + ${#DEPLOY_FAILURES[@]} ))

echo ""
echo "================================================================"
echo " Deployment Summary Report"
echo "================================================================"
if [ "${#DEPLOY_SUCCESSES[@]}" -gt 0 ]; then
    for succ in "${DEPLOY_SUCCESSES[@]}"; do
        echo "  [SUCCESS] $succ"
    done
fi
if [ "${#DEPLOY_FAILURES[@]}" -gt 0 ]; then
    for fail in "${DEPLOY_FAILURES[@]}"; do
        echo "  [FAILED]  $fail"
    done
fi
echo "----------------------------------------------------------------"
echo " Total: $TOTAL_DEPLOYED | Succeeded: ${#DEPLOY_SUCCESSES[@]} | Failed: ${#DEPLOY_FAILURES[@]}"
echo "================================================================"

if [ "${#DEPLOY_FAILURES[@]}" -gt 0 ]; then
    exit 1
fi
exit 0
