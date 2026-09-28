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
DEFAULT_PROJECT="${PROJECT:-madtom-daemon}"
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
# Known MADTOM Go Project Specifications (cmd/<directory>)
# Format: "canonical_name:alias1:alias2:binary_path:service_name:dest_path:unit_file"
# ------------------------------------------------------------------------------
PROJECT_SPECS=(
    "madtom-daemon:daemon:madtomd:bin/madtom-daemon:madtomd.service:/opt/madtomd:systemd/madtom-daemon.service"
    "madtom-collector:collector::bin/madtom-collector:madtom-collector.service:/opt/madtom-collector:systemd/madtom-collector.service"
    "squeeze-server:squeeze:madtom-squeeze:bin/squeeze-server:squeeze-server.service:/opt/squeeze-server:systemd/squeeze-server.service"
    "madtom-tsdb-merge:merge:tsdb-merge:bin/madtom-tsdb-merge:madtom-tsdb-merge.service:/opt/madtom-tsdb-merge:systemd/madtom-tsdb-merge.service"
)

resolve_project_spec() {
    local query="${1,,}"
    local default_name="${1}"
    
    for spec in "${PROJECT_SPECS[@]}"; do
        IFS=':' read -r p_name p_a1 p_a2 p_bin p_service p_dest p_unit <<< "$spec"
        if [ "$query" = "$p_name" ] || [ "$query" = "$p_a1" ] || [ "$query" = "$p_a2" ]; then
            RESOLVED_PROJECT_NAME="$p_name"
            RESOLVED_PROJECT_BIN="$p_bin"
            RESOLVED_PROJECT_SERVICE="$p_service"
            RESOLVED_PROJECT_DEST="$p_dest"
            RESOLVED_PROJECT_UNIT="${p_unit:-systemd/${p_name}.service}"
            return 0
        fi
    done

    # Dynamic fallback to directory under cmd/
    RESOLVED_PROJECT_NAME="$default_name"
    RESOLVED_PROJECT_BIN="bin/$default_name"
    RESOLVED_PROJECT_SERVICE="${default_name}.service"
    RESOLVED_PROJECT_DEST="/opt/$default_name"
    RESOLVED_PROJECT_UNIT="systemd/${default_name}.service"
    return 0
}

find_local_unit_file() {
    local svc="$1"
    local proj="${2:-}"
    local custom_file="${3:-}"

    if [ -n "$custom_file" ] && [ -f "$custom_file" ]; then
        echo "$custom_file"
        return 0
    fi

    local proj_unit=""
    if [ -n "$proj" ]; then
        if resolve_project_spec "$proj" 2>/dev/null; then
            proj_unit="$RESOLVED_PROJECT_UNIT"
        fi
    fi

    local candidates=(
        "${SCRIPT_DIR}/${proj_unit}"
        "${REPO_ROOT}/MADTOM_GOLANG/${proj_unit}"
        "${SCRIPT_DIR}/systemd/${svc}"
        "${REPO_ROOT}/MADTOM_GOLANG/systemd/${svc}"
        "${SCRIPT_DIR}/systemd/${svc%.service}.service"
        "${REPO_ROOT}/MADTOM_GOLANG/systemd/${svc%.service}.service"
        "${SCRIPT_DIR}/systemd/${proj}.service"
        "${REPO_ROOT}/MADTOM_GOLANG/systemd/${proj}.service"
        "${SCRIPT_DIR}/systemd/madtom-${proj}.service"
        "${REPO_ROOT}/MADTOM_GOLANG/systemd/madtom-${proj}.service"
    )

    for f in "${candidates[@]}"; do
        if [ -n "$f" ] && [ -f "$f" ]; then
            echo "$f"
            return 0
        fi
    done
    return 1
}

list_available_projects() {
    echo "================================================================================"
    echo " Available MADTOM Go Projects (MADTOM_GOLANG/cmd)"
    echo "================================================================================"
    printf "  %-24s %-24s %-28s %-20s\n" "Project (Aliases)" "Default Binary" "Default Service" "Default Destination"
    printf "  %-24s %-24s %-28s %-20s\n" "------------------------" "------------------------" "----------------------------" "--------------------"
    for spec in "${PROJECT_SPECS[@]}"; do
        IFS=':' read -r p_name p_a1 p_a2 p_bin p_service p_dest <<< "$spec"
        local display_name="$p_name"
        if [ -n "$p_a1" ]; then display_name="${display_name} (${p_a1})"; fi
        printf "  %-24s %-24s %-28s %-20s\n" "$display_name" "$p_bin" "$p_service" "$p_dest"
    done
    echo "================================================================================"
}

# ------------------------------------------------------------------------------
# SSH Host & User Resolution Helper (~/.ssh/config short names support)
# ------------------------------------------------------------------------------
resolve_ssh_credentials_and_host() {
    local host="$1"
    local user="$2"
    local port="$3"

    local ssh_cfg_user=""
    local ssh_cfg_port=""
    if command -v ssh >/dev/null 2>&1 && [ -n "$host" ]; then
        local ssh_g
        ssh_g="$(ssh -G "$host" 2>/dev/null || true)"
        if [ -n "$ssh_g" ]; then
            ssh_cfg_user="$(echo "$ssh_g" | awk '/^user / {print $2; exit}')"
            ssh_cfg_port="$(echo "$ssh_g" | awk '/^port / {print $2; exit}')"
        fi
    fi

    RESOLVED_USER="${user:-${ssh_cfg_user:-${USER:-$(id -un)}}}"
    RESOLVED_PORT="${port:-${ssh_cfg_port:-$DEFAULT_SSH_PORT}}"
}

# ------------------------------------------------------------------------------
# Usage Information
# ------------------------------------------------------------------------------
print_usage() {
    cat << EOF
Usage: $(basename "$0") [OPTIONS] [USER@HOST | HOST | USER HOST]
       $(basename "$0") -c config.yaml [OPTIONS]

Deploy MADTOM Go backend executable to one or more remote systems via SSH.

Authentication:
  - Supports SSH public keys and ssh-agent automatically (no password required).
  - Automatically checks for remote passwordless sudo (no sudo password required).
  - Supports SSH short names and host aliases defined in ~/.ssh/config.

Options:
  -P, --project NAME    Project to build & deploy (default: madtom-daemon)
                          Known projects: madtom-daemon (daemon), madtom-collector (collector),
                          squeeze-server (squeeze), madtom-tsdb-merge (merge), or any dir in cmd/
  --list-projects       List available Go projects and their default services/binaries
  -c, --config PATH     Path to YAML configuration file for multi-host deployment
  -u, --user USER       SSH username (defaults to ~/.ssh/config User or current user)
  -h, --host HOST       Remote hostname, IP address, or SSH config alias (e.g. tp1)
  -p, --port PORT       SSH port (default: from ~/.ssh/config or $DEFAULT_SSH_PORT)
  -b, --binary PATH     Local binary to deploy (default: derived from project)
  -d, --dest PATH       Remote destination path (default: derived from project)
  -s, --service NAME    Remote systemd service name (default: derived from project)
  -a, --arch ARCH       Target architecture:
                          - amd64   (x86_64)
                          - arm64   (ARM 64-bit / aarch64)
                          - arm     (ARM 32-bit v7 / armhf)
                          - auto    (detect remote architecture via SSH, default)
  -i, --install-service Install or overwrite remote systemd service unit file (auto if nonexistent)
  --service-file PATH   Explicit local path to systemd service unit file to install
  --ssh-pass PASS       SSH login password (only needed if SSH key auth is not set up)
  --sudo-pass PASS      Remote sudo password (defaults to --ssh-pass or empty if passwordless)
  --build               Build the Go binary locally before deploying
  --dry-run             Validate and print deployment targets without executing
  --help                Show this help message and exit

Environment Variables:
  PROJECT                    Default project to build & deploy
  SSH_PASS / SSH_PASSWORD    Default SSH password
  SUDO_PASS / SUDO_PASSWORD  Default remote sudo password
  CONFIG_FILE                Default config file path

Examples:
  # Deploy daemon to an SSH config alias (uses SSH key & passwordless sudo):
  $(basename "$0") tp1

  # Deploy squeeze server to tp1 (automatically installs systemd service if missing):
  $(basename "$0") -P squeeze tp1

  # Deploy collector to a remote node:
  $(basename "$0") -P collector realiteam.art

  # Deploy to multiple servers defined in deploy.yaml:
  $(basename "$0") -c deploy.yaml

  # Single host with password arguments (if not using SSH keys):
  $(basename "$0") -u danial -h la.realiteam.art --ssh-pass secret123 --sudo-pass secret123

  # Single host with custom service name and auto-probe build:
  $(basename "$0") danial@oc1.realiteam.art -s madtom-custom.service --build
EOF
}

# ------------------------------------------------------------------------------
# Argument Parsing
# ------------------------------------------------------------------------------
CONFIG_FILE="${CONFIG_FILE:-}"
CLI_PROJECT="${PROJECT:-}"
CLI_PROJECT_SET=0
CLI_USER=""
CLI_HOST=""
CLI_PORT=""
CLI_BINARY=""
CLI_DEST=""
CLI_SERVICE=""
CLI_ARCH=""
CLI_BUILD=0
CLI_INSTALL_SERVICE=0
CLI_SERVICE_FILE=""
CLI_SSH_PASS=""
CLI_SUDO_PASS=""
DRY_RUN=0

CLI_SERVICE_SET=0
CLI_BUILD_SET=0
CLI_BINARY_SET=0
CLI_DEST_SET=0
CLI_ARCH_SET=0
POSITIONAL_ARGS=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        -P|--project|--app)
            CLI_PROJECT="$2"
            CLI_PROJECT_SET=1
            shift 2
            ;;
        --list-projects)
            list_available_projects
            exit 0
            ;;
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
        -i|--install-service)
            CLI_INSTALL_SERVICE=1
            shift
            ;;
        --service-file)
            CLI_SERVICE_FILE="$2"
            CLI_INSTALL_SERVICE=1
            shift 2
            ;;
        --dry-run)
            DRY_RUN=1
            shift
            ;;
        --help)
            print_usage
            exit 0
            ;;
        -*)
            echo "Error: Unknown option: $1" >&2
            print_usage
            exit 1
            ;;
        *)
            POSITIONAL_ARGS+=("$1")
            shift
            ;;
    esac
done

# Resolve positional arguments [USER@HOST | HOST | USER HOST]
if [ "${#POSITIONAL_ARGS[@]}" -gt 0 ]; then
    if [ -z "$CLI_HOST" ]; then
        if [ "${#POSITIONAL_ARGS[@]}" -eq 1 ]; then
            raw_arg="${POSITIONAL_ARGS[0]}"
            if [[ "$raw_arg" == *"@"* ]]; then
                CLI_USER="${raw_arg%%@*}"
                CLI_HOST="${raw_arg##*@}"
            else
                CLI_HOST="$raw_arg"
            fi
        else
            CLI_USER="${POSITIONAL_ARGS[0]}"
            CLI_HOST="${POSITIONAL_ARGS[1]}"
        fi
    elif [ -z "$CLI_USER" ]; then
        CLI_USER="${POSITIONAL_ARGS[0]}"
    fi
fi

# Apply project defaults if -P / --project was specified
if [ "$CLI_PROJECT_SET" -eq 1 ]; then
    resolve_project_spec "$CLI_PROJECT"
    if [ "$CLI_BINARY_SET" -eq 0 ]; then
        DEFAULT_LOCAL_BIN="$RESOLVED_PROJECT_BIN"
    fi
    if [ "$CLI_SERVICE_SET" -eq 0 ]; then
        DEFAULT_SERVICE_NAME="$RESOLVED_PROJECT_SERVICE"
    fi
    if [ "$CLI_DEST_SET" -eq 0 ]; then
        DEFAULT_DEST_PATH="$RESOLVED_PROJECT_DEST"
    fi
fi

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

    resolve_project_spec "$bin_name"
    local proj_name="$RESOLVED_PROJECT_NAME"

    if [ -d "$SCRIPT_DIR/cmd/$proj_name" ]; then
        cmd_dir="./cmd/$proj_name"
    elif [ -d "$REPO_ROOT/MADTOM_GOLANG/cmd/$proj_name" ]; then
        go_src_dir="$REPO_ROOT/MADTOM_GOLANG"
        cmd_dir="./cmd/$proj_name"
    elif [ -d "$SCRIPT_DIR/cmd/$bin_name" ]; then
        cmd_dir="./cmd/$bin_name"
    elif [ -d "$REPO_ROOT/MADTOM_GOLANG/cmd/$bin_name" ]; then
        go_src_dir="$REPO_ROOT/MADTOM_GOLANG"
        cmd_dir="./cmd/$bin_name"
    elif [ "$bin_name" = "madtom-squeeze" ] || [ "$bin_name" = "squeeze-server" ]; then
        cmd_dir="./cmd/squeeze-server"
    elif [ -d "$SCRIPT_DIR/cmd/madtom-daemon" ]; then
        cmd_dir="./cmd/madtom-daemon"
    elif [ -d "$REPO_ROOT/MADTOM_GOLANG/cmd/madtom-daemon" ]; then
        go_src_dir="$REPO_ROOT/MADTOM_GOLANG"
        cmd_dir="./cmd/madtom-daemon"
    fi

    if [ -z "$cmd_dir" ]; then
        echo "Error: Cannot find Go source to build $bin_name (checked cmd/$proj_name, cmd/$bin_name, cmd/madtom-daemon)." >&2
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
    local project="${11:-}"
    local install_service_flag="${12:-$CLI_INSTALL_SERVICE}"

    # Resolve SSH config parameters if host is defined in ~/.ssh/config or system config
    resolve_ssh_credentials_and_host "$host" "$user" "$port"
    user="$RESOLVED_USER"
    port="$RESOLVED_PORT"

    if [ -z "$host" ]; then
        echo "Error: Host must be defined." >&2
        return 1
    fi

    # Test if SSH key authentication is available and functional
    local has_ssh_key_auth=0
    local has_sudo_nopass=0

    if [ -z "$ssh_pass" ]; then
        if ssh -o BatchMode=yes \
               -o ConnectTimeout=5 \
               -o StrictHostKeyChecking=accept-new \
               -p "$port" \
               "${user}@${host}" "exit 0" >/dev/null 2>&1; then
            has_ssh_key_auth=1
            echo "==> SSH key authentication verified for ${user}@${host} (no password required)."
        fi
    fi

    # If key auth works and no sudo password was provided, test if passwordless sudo works
    if [ "$has_ssh_key_auth" -eq 1 ] && [ -z "$sudo_pass" ]; then
        if ssh -o BatchMode=yes \
               -o ConnectTimeout=5 \
               -o StrictHostKeyChecking=accept-new \
               -p "$port" \
               "${user}@${host}" "sudo -n true" >/dev/null 2>&1; then
            has_sudo_nopass=1
            sudo_pass=""
            echo "    Remote passwordless sudo verified (no sudo password required)."
        fi
    fi

    # Interactive password prompt only if authentication cannot be satisfied automatically
    if [ "$has_ssh_key_auth" -eq 0 ] && [ -z "$ssh_pass" ] && [ -t 0 ]; then
        read -r -s -p "Enter SSH password for ${user}@${host} (leave empty to attempt key auth): " ssh_pass
        echo ""
        if [ -n "$ssh_pass" ] && [ -z "$sudo_pass" ]; then
            read -r -s -p "Enter Sudo password (press Enter to use SSH password): " sudo_pass
            echo ""
            sudo_pass="${sudo_pass:-$ssh_pass}"
        fi
    fi

    # If SSH key worked but remote sudo requires password and none was provided
    if [ "$has_ssh_key_auth" -eq 1 ] && [ "$has_sudo_nopass" -eq 0 ] && [ -z "$sudo_pass" ] && [ -t 0 ]; then
        read -r -s -p "Enter remote sudo password for ${user}@${host} (leave empty if passwordless): " sudo_pass
        echo ""
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

    # Infer project name if not explicitly passed
    if [ -z "$project" ]; then
        case "$service" in
            *squeeze*) project="squeeze-server" ;;
            *collector*) project="madtom-collector" ;;
            *daemon*|*madtomd*) project="madtom-daemon" ;;
            *merge*) project="madtom-tsdb-merge" ;;
            *) project="$bin_name" ;;
        esac
    fi

    # Check if systemd service unit already exists on remote system
    local remote_svc_exists=0
    if run_ssh "systemctl cat '$service' >/dev/null 2>&1"; then
        remote_svc_exists=1
    fi

    local should_install_unit=0
    local local_unit_file=""
    if [ "$remote_svc_exists" -eq 0 ] || [ "$install_service_flag" -eq 1 ]; then
        local_unit_file="$(find_local_unit_file "$service" "$project" "$CLI_SERVICE_FILE" || true)"
        if [ -n "$local_unit_file" ] && [ -f "$local_unit_file" ]; then
            should_install_unit=1
        elif [ "$remote_svc_exists" -eq 0 ]; then
            echo "    Notice: Service '$service' does not exist remotely and no matching local unit file found in systemd/."
        fi
    fi

    echo "----------------------------------------------------------------"
    echo "  Host         : ${user}@${host}:${port}"
    echo "  Architecture : linux/${target_arch}"
    echo "  Local Binary : $local_bin ($bin_size)"
    echo "  Destination  : $target_bin"
    echo "  Service Name : $service"
    if [ "$remote_svc_exists" -eq 0 ]; then
        if [ "$should_install_unit" -eq 1 ]; then
            echo "  Service Unit : Nonexistent on remote -> Auto-installing from $local_unit_file"
        else
            echo "  Service Unit : Nonexistent on remote (No local unit found)"
        fi
    elif [ "$install_service_flag" -eq 1 ]; then
        echo "  Service Unit : Updating from $local_unit_file"
    fi
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

    # Step 1b: Upload service unit file if missing or requested
    local remote_svc_tmp=""
    if [ "$should_install_unit" -eq 1 ]; then
        remote_svc_tmp="${DEFAULT_REMOTE_TMP_DIR}/madtom_svc_$(date +%s)_$RANDOM.service"
        echo "==> Uploading systemd service unit: $local_unit_file ..."
        run_scp "$local_unit_file" "${user}@${host}:${remote_svc_tmp}"
    fi

    # Step 1c: Upload companion configuration if applicable (e.g. Squeeze)
    local remote_cfg_tmp=""
    local local_cfg_file=""
    if [ "$project" = "squeeze-server" ] || [ "$project" = "squeeze" ] || [ "$service" = "squeeze-server.service" ]; then
        local_cfg_file="${SCRIPT_DIR}/configs/squeeze/squeeze.yaml"
        if [ ! -f "$local_cfg_file" ]; then
            local_cfg_file="${REPO_ROOT}/MADTOM_GOLANG/configs/squeeze/squeeze.yaml"
        fi
        if [ -f "$local_cfg_file" ]; then
            remote_cfg_tmp="${DEFAULT_REMOTE_TMP_DIR}/madtom_cfg_$(date +%s)_$RANDOM.yaml"
            echo "==> Uploading default squeeze config: $local_cfg_file ..."
            run_scp "$local_cfg_file" "${user}@${host}:${remote_cfg_tmp}"
        fi
    fi

    # Step 2: Install via sudo & Step 3: Restart systemd service
    echo "==> [2/3] Installing binary to $target_bin with sudo ..."
    echo "==> [3/3] Configuring and restarting systemd service: $service ..."

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
REMOTE_TMP="$remote_tmp"
REMOTE_SVC_TMP="$remote_svc_tmp"
REMOTE_CFG_TMP="$remote_cfg_tmp"
SERVICE_NAME="$service"
PROJECT_NAME="$project"

# Clean up stale regular file if target dir exists as a file
if [ -f "\$TARGET_DIR" ]; then
    echo "    Cleaning up existing regular file at directory path: \$TARGET_DIR"
    run_sudo rm -f "\$TARGET_DIR"
fi

if [ ! -d "\$TARGET_DIR" ]; then
    echo "    Creating destination directory: \$TARGET_DIR"
    run_sudo mkdir -p "\$TARGET_DIR"
fi

echo "    Copying binary to \$TARGET_BIN (sudo)"
run_sudo cp -f "\$REMOTE_TMP" "\${TARGET_BIN}.new"
run_sudo chmod 755 "\${TARGET_BIN}.new"
run_sudo chown root:root "\${TARGET_BIN}.new" 2>/dev/null || true
run_sudo mv -f "\${TARGET_BIN}.new" "\$TARGET_BIN"
rm -f "\$REMOTE_TMP"

# Pre-create companion directories and install initial config if needed
if [ "\$PROJECT_NAME" = "squeeze-server" ] || [ "\$PROJECT_NAME" = "squeeze" ] || [ "\$SERVICE_NAME" = "squeeze-server.service" ]; then
    run_sudo mkdir -p /var/lib/squeeze/scratch /var/lib/squeeze/output /etc/squeeze
    if ! command -v ffmpeg >/dev/null 2>&1 || ! command -v ffprobe >/dev/null 2>&1; then
        echo "    Notice: ffmpeg/ffprobe not found on remote. Attempting installation via package manager..."
        if command -v apt-get >/dev/null 2>&1; then
            DEBIAN_FRONTEND=noninteractive run_sudo apt-get update -qq && DEBIAN_FRONTEND=noninteractive run_sudo apt-get install -y -qq ffmpeg || true
        elif command -v dnf >/dev/null 2>&1; then
            run_sudo dnf install -y -q ffmpeg || true
        elif command -v pacman >/dev/null 2>&1; then
            run_sudo pacman -S --noconfirm ffmpeg || true
        fi
    fi
    if [ -n "\$REMOTE_CFG_TMP" ] && [ -f "\$REMOTE_CFG_TMP" ]; then
        if [ ! -f /etc/squeeze/squeeze.yaml ]; then
            echo "    Installing initial configuration: /etc/squeeze/squeeze.yaml"
            run_sudo cp -f "\$REMOTE_CFG_TMP" /etc/squeeze/squeeze.yaml
            run_sudo chmod 644 /etc/squeeze/squeeze.yaml
            run_sudo chown root:root /etc/squeeze/squeeze.yaml 2>/dev/null || true
        else
            echo "    Existing /etc/squeeze/squeeze.yaml preserved."
        fi
        rm -f "\$REMOTE_CFG_TMP"
    fi
elif [ "\$PROJECT_NAME" = "madtom-collector" ] || [ "\$PROJECT_NAME" = "collector" ] || [ "\$SERVICE_NAME" = "madtom-collector.service" ]; then
    run_sudo mkdir -p /var/lib/madtom-collector/data
elif [ "\$PROJECT_NAME" = "madtom-daemon" ] || [ "\$PROJECT_NAME" = "daemon" ] || [ "\$SERVICE_NAME" = "madtomd.service" ]; then
    run_sudo mkdir -p /var/lib/madtomd/wal
fi

# Install systemd service unit file if staged
if [ -n "\$REMOTE_SVC_TMP" ] && [ -f "\$REMOTE_SVC_TMP" ]; then
    echo "    Installing systemd unit file to /etc/systemd/system/\$SERVICE_NAME"
    run_sudo cp -f "\$REMOTE_SVC_TMP" "/etc/systemd/system/\$SERVICE_NAME"
    run_sudo chmod 644 "/etc/systemd/system/\$SERVICE_NAME"
    run_sudo chown root:root "/etc/systemd/system/\$SERVICE_NAME" 2>/dev/null || true
    rm -f "\$REMOTE_SVC_TMP"
    if command -v systemctl >/dev/null 2>&1; then
        run_sudo systemctl daemon-reload
        run_sudo systemctl enable "\$SERVICE_NAME" 2>/dev/null || true
    fi
fi

# Systemd operations
if command -v systemctl >/dev/null 2>&1; then
    SERVICE_EXEC="\$(run_sudo systemctl cat "\$SERVICE_NAME" 2>/dev/null | grep -E '^\s*ExecStart=' | head -n1 | awk '{print \$1}' | sed 's/^\s*ExecStart=//' || true)"
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
    echo "    Restarting service: \$SERVICE_NAME"
    run_sudo systemctl restart "\$SERVICE_NAME"

    echo "    Verifying service status..."
    if run_sudo systemctl is-active --quiet "\$SERVICE_NAME"; then
        echo "    Status: ACTIVE (Running)"
    else
        echo "    Warning: Service '\$SERVICE_NAME' is not active. Recent service journal:"
        run_sudo journalctl -u "\$SERVICE_NAME" -n 20 --no-pager || true
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

project_specs = {
    'madtom-daemon': ('bin/madtom-daemon', 'madtomd.service', '/opt/madtomd'),
    'daemon': ('bin/madtom-daemon', 'madtomd.service', '/opt/madtomd'),
    'madtomd': ('bin/madtom-daemon', 'madtomd.service', '/opt/madtomd'),
    'madtom-collector': ('bin/madtom-collector', 'madtom-collector.service', '/opt/madtom-collector'),
    'collector': ('bin/madtom-collector', 'madtom-collector.service', '/opt/madtom-collector'),
    'squeeze-server': ('bin/squeeze-server', 'squeeze-server.service', '/opt/squeeze-server'),
    'squeeze': ('bin/squeeze-server', 'squeeze-server.service', '/opt/squeeze-server'),
    'madtom-squeeze': ('bin/squeeze-server', 'squeeze-server.service', '/opt/squeeze-server'),
    'madtom-tsdb-merge': ('bin/madtom-tsdb-merge', 'madtom-tsdb-merge.service', '/opt/madtom-tsdb-merge'),
    'merge': ('bin/madtom-tsdb-merge', 'madtom-tsdb-merge.service', '/opt/madtom-tsdb-merge'),
    'tsdb-merge': ('bin/madtom-tsdb-merge', 'madtom-tsdb-merge.service', '/opt/madtom-tsdb-merge')
}

default_proj = str(defaults.get('project', '')).strip().lower()

for s in raw_servers:
    if isinstance(s, str):
        h = s.strip()
        u = defaults.get('user', '')
        p = defaults.get('port', 0)
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

    proj = str(s.get('project') or default_proj).strip().lower()
    proj_bin, proj_svc, proj_dest = '', '', ''
    if proj:
        if proj in project_specs:
            proj_bin, proj_svc, proj_dest = project_specs[proj]
        else:
            proj_bin = f'bin/{proj}'
            proj_svc = f'{proj}.service'
            proj_dest = f'/opt/{proj}'

    user = str(s.get('user') or defaults.get('user', '')).strip()
    port = s.get('port') or defaults.get('port', 0)
    service = str(s.get('service') or defaults.get('service', '') or proj_svc or 'madtomd.service').strip()
    dest = str(s.get('dest') or defaults.get('dest', '') or proj_dest or '/opt/madtomd').strip()
    binary = str(s.get('binary') or defaults.get('binary', '') or proj_bin or 'bin/madtom-daemon').strip()
    arch = str(s.get('arch') or defaults.get('arch', 'auto')).strip()
    build = s.get('build', defaults.get('build', False))
    if isinstance(build, str):
        build = build.lower() in ('true', '1', 'yes')

    ssh_pass = s.get('ssh_pass') or s.get('ssh_password') or s.get('password') or defaults.get('ssh_pass') or defaults.get('ssh_password') or defaults.get('password') or ''
    sudo_pass = s.get('sudo_pass') or s.get('sudo_password') or defaults.get('sudo_pass') or defaults.get('sudo_password') or ''

    entry = {
        'host': host,
        'user': user,
        'port': int(port),
        'project': proj,
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

        # Project resolution if CLI -P was specified
        if [ "$CLI_PROJECT_SET" -eq 1 ]; then
            resolve_project_spec "$CLI_PROJECT"
            TARGET_PROJECT="$RESOLVED_PROJECT_NAME"
            TARGET_SERVICE="${CLI_SERVICE:-$RESOLVED_PROJECT_SERVICE}"
            TARGET_DEST="${CLI_DEST:-$RESOLVED_PROJECT_DEST}"
            TARGET_BIN="${CLI_BINARY:-$RESOLVED_PROJECT_BIN}"
        else
            TARGET_PROJECT="${S_PROJECT:-}"
            TARGET_SERVICE="$S_SERVICE"
            TARGET_DEST="$S_DEST"
            TARGET_BIN="$S_BINARY"
            if [ "$CLI_SERVICE_SET" -eq 1 ]; then TARGET_SERVICE="$CLI_SERVICE"; fi
            if [ "$CLI_DEST_SET" -eq 1 ]; then TARGET_DEST="$CLI_DEST"; fi
            if [ "$CLI_BINARY_SET" -eq 1 ]; then TARGET_BIN="$CLI_BINARY"; fi
        fi

        TARGET_HOST="$S_HOST"
        TARGET_USER="${CLI_USER:-$S_USER}"
        TARGET_PORT="${CLI_PORT:-$S_PORT}"
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

        # Resolve host alias / SSH config details
        resolve_ssh_credentials_and_host "$TARGET_HOST" "$TARGET_USER" "$TARGET_PORT"
        TARGET_USER="$RESOLVED_USER"
        TARGET_PORT="$RESOLVED_PORT"

        echo ""
        echo "================================================================"
        echo " [$CURRENT_IDX/$TOTAL_SERVERS] Host: ${TARGET_USER}@${TARGET_HOST}:${TARGET_PORT}"
        if [ -n "$TARGET_PROJECT" ]; then
            echo " Project: $TARGET_PROJECT"
        fi
        echo "================================================================"

        TARGET_INSTALL_SERVICE=0
        if [ "${S_INSTALL_SERVICE:-}" = "True" ] || [ "${S_INSTALL_SERVICE:-}" = "1" ] || [ "$CLI_INSTALL_SERVICE" -eq 1 ]; then
            TARGET_INSTALL_SERVICE=1
        fi

        if deploy_single_host "$TARGET_HOST" "$TARGET_USER" "$TARGET_PORT" \
                              "$TARGET_SERVICE" "$TARGET_DEST" "$TARGET_BIN" \
                              "$TARGET_ARCH" "$TARGET_BUILD" \
                              "$TARGET_SSH_PASS" "$TARGET_SUDO_PASS" \
                              "$TARGET_PROJECT" "$TARGET_INSTALL_SERVICE"; then
            DEPLOY_SUCCESSES+=("${TARGET_USER}@${TARGET_HOST} (service: ${TARGET_SERVICE})")
        else
            DEPLOY_FAILURES+=("${TARGET_USER}@${TARGET_HOST} (service: ${TARGET_SERVICE})")
        fi
    done

else
    # Single-host mode
    TARGET_HOST="$CLI_HOST"
    TARGET_USER="$CLI_USER"
    TARGET_PORT="${CLI_PORT:-}"

    if [ -n "$TARGET_HOST" ]; then
        resolve_ssh_credentials_and_host "$TARGET_HOST" "$TARGET_USER" "$TARGET_PORT"
        TARGET_USER="$RESOLVED_USER"
        TARGET_PORT="$RESOLVED_PORT"
    fi

    if [ -z "$TARGET_HOST" ] && [ -t 0 ]; then
        read -r -p "Enter remote host / IP or SSH alias: " TARGET_HOST
        if [ -n "$TARGET_HOST" ]; then
            resolve_ssh_credentials_and_host "$TARGET_HOST" "$TARGET_USER" "$TARGET_PORT"
            TARGET_USER="$RESOLVED_USER"
            TARGET_PORT="$RESOLVED_PORT"
        fi
    fi

    if [ -z "$TARGET_USER" ] && [ -t 0 ]; then
        read -r -p "Enter remote SSH username (press Enter for '${USER:-$(id -un)}'): " TARGET_USER
        TARGET_USER="${TARGET_USER:-${USER:-$(id -un)}}"
    fi

    if [ -z "$TARGET_HOST" ]; then
        echo "Error: Remote host is required." >&2
        print_usage
        exit 1
    fi

    TARGET_SERVICE="${CLI_SERVICE:-$DEFAULT_SERVICE_NAME}"
    TARGET_DEST="${CLI_DEST:-$DEFAULT_DEST_PATH}"
    TARGET_BIN="${CLI_BINARY:-$DEFAULT_LOCAL_BIN}"
    TARGET_ARCH="${CLI_ARCH:-auto}"
    TARGET_BUILD="$CLI_BUILD"
    TARGET_SSH_PASS="${CLI_SSH_PASS:-$DEFAULT_SSH_PASS}"
    TARGET_SUDO_PASS="${CLI_SUDO_PASS:-${DEFAULT_SUDO_PASS:-$TARGET_SSH_PASS}}"

    echo "================================================================"
    echo " MADTOM Deployment - Single Host: ${TARGET_USER}@${TARGET_HOST}"
    if [ -n "$CLI_PROJECT" ]; then
        echo " Project: $CLI_PROJECT"
    fi
    echo "================================================================"

    if deploy_single_host "$TARGET_HOST" "$TARGET_USER" "$TARGET_PORT" \
                          "$TARGET_SERVICE" "$TARGET_DEST" "$TARGET_BIN" \
                          "$TARGET_ARCH" "$TARGET_BUILD" \
                          "$TARGET_SSH_PASS" "$TARGET_SUDO_PASS" \
                          "${CLI_PROJECT:-}" "$CLI_INSTALL_SERVICE"; then
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
