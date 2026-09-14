#!/usr/bin/env bash
# ==============================================================================
# MADTOM Go Backend Deployment Script
# ==============================================================================
# This script deploys the Go backend binary to a remote host via SSH/SCP,
# copies it into the final destination directory using sudo, and restarts
# the specified systemd service.
# ==============================================================================

set -euo pipefail

# ------------------------------------------------------------------------------
# Top Configuration (Edit these default values as needed)
# ------------------------------------------------------------------------------

# Destination path on the remote host (e.g., /opt/madtomd or /opt/madtom/madtom-collector)
DEST_PATH="${DEST_PATH:-/opt/madtomd}"

# Systemd service name to restart on the remote host
SERVICE_NAME="${SERVICE_NAME:-madtomd.service}"

# Local executable path to deploy (relative to script or absolute)
LOCAL_BIN="${LOCAL_BIN:-bin/madtom-daemon}"

# Remote temporary directory used for the initial upload
REMOTE_TMP_DIR="${REMOTE_TMP_DIR:-/tmp}"

# Default SSH port
SSH_PORT="${SSH_PORT:-22}"

# Host architecture detection
HOST_UNAME="$(uname -m)"
case "$HOST_UNAME" in
    x86_64)               DEFAULT_ARCH="amd64" ;;
    aarch64|arm64|armv8*) DEFAULT_ARCH="arm64" ;;
    armv7*|armv6*|armhf)  DEFAULT_ARCH="arm" ;;
    *)                    DEFAULT_ARCH="amd64" ;;
esac

TARGET_ARCH="auto"

# ------------------------------------------------------------------------------
# Usage & Argument Parsing
# ------------------------------------------------------------------------------

print_usage() {
    cat << EOF
Usage: $(basename "$0") [OPTIONS] [USER@HOST | USER HOST]

Deploy MADTOM Go backend executable to a remote system via SSH.

Options:
  -u, --user USER       SSH username
  -h, --host HOST       Remote hostname or IP address
  -p, --port PORT       SSH port (default: $SSH_PORT)
  -b, --binary PATH     Local binary to deploy (default: $LOCAL_BIN)
  -d, --dest PATH       Remote destination path (default: $DEST_PATH)
  -s, --service NAME    Remote systemd service name (default: $SERVICE_NAME)
  -a, --arch ARCH       Target architecture:
                          - amd64   (x86_64)
                          - arm64   (ARM 64-bit / aarch64)
                          - arm     (ARM 32-bit v7 / armhf)
                          - auto    (detect remote architecture via SSH, default)
  --build               Build the Go binary locally before deploying
  --help                Show this help message and exit

Examples:
  $(basename "$0") root@192.168.1.100
  $(basename "$0") -u danial -h 10.0.0.12 -a arm64 --build
  $(basename "$0") -u danial -h 10.0.0.12 -b bin/madtom-collector -d /opt/madtom-collector -s madtom-collector.service
EOF
}

SSH_USER=""
SSH_HOST=""
DO_BUILD=0

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        -u|--user)
            SSH_USER="$2"
            shift 2
            ;;
        -h|--host)
            SSH_HOST="$2"
            shift 2
            ;;
        -p|--port)
            SSH_PORT="$2"
            shift 2
            ;;
        -b|--binary)
            LOCAL_BIN="$2"
            shift 2
            ;;
        -d|--dest)
            DEST_PATH="$2"
            shift 2
            ;;
        -s|--service)
            SERVICE_NAME="$2"
            shift 2
            ;;
        -a|--arch)
            case "${2,,}" in
                amd64|x86_64|x64)          TARGET_ARCH="amd64" ;;
                arm64|aarch64|linux-arm64) TARGET_ARCH="arm64" ;;
                arm|armv7|armhf|linux-arm) TARGET_ARCH="arm" ;;
                auto)                      TARGET_ARCH="auto" ;;
                *)                         TARGET_ARCH="$2" ;;
            esac
            shift 2
            ;;
        --build)
            DO_BUILD=1
            shift
            ;;
        --help)
            print_usage
            exit 0
            ;;
        *@*)
            SSH_USER="${1%%@*}"
            SSH_HOST="${1##*@}"
            shift
            ;;
        *)
            if [ -z "$SSH_USER" ]; then
                SSH_USER="$1"
            elif [ -z "$SSH_HOST" ]; then
                SSH_HOST="$1"
            else
                echo "Unknown argument: $1" >&2
                print_usage
                exit 1
            fi
            shift
            ;;
    esac
done

# ------------------------------------------------------------------------------
# Interactive Prompts (when missing user, host, or password)
# ------------------------------------------------------------------------------

# Resolve script directory to locate binaries relative to Go module if needed
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ ! -f "$LOCAL_BIN" ] && [ -f "$SCRIPT_DIR/$LOCAL_BIN" ]; then
    LOCAL_BIN="$SCRIPT_DIR/$LOCAL_BIN"
fi

if [ -z "$SSH_USER" ]; then
    read -r -p "Enter remote SSH username: " SSH_USER
fi

if [ -z "$SSH_HOST" ]; then
    read -r -p "Enter remote host / IP: " SSH_HOST
fi

if [ -z "$SSH_USER" ] || [ -z "$SSH_HOST" ]; then
    echo "Error: Both SSH user and host are required." >&2
    exit 1
fi

# Prompt for password interactively without echoing
SSH_PASS="${SSH_PASSWORD:-}"
if [ -z "$SSH_PASS" ]; then
    read -r -s -p "Enter SSH & Sudo password for ${SSH_USER}@${SSH_HOST}: " SSH_PASS
    echo ""
fi

if [ -z "$SSH_PASS" ]; then
    echo "Error: Password cannot be empty." >&2
    exit 1
fi

# ------------------------------------------------------------------------------
# SSH & SCP Execution Wrapper (uses sshpass or secure SSH_ASKPASS)
# ------------------------------------------------------------------------------

ASKPASS_SCRIPT=""

cleanup() {
    if [ -n "$ASKPASS_SCRIPT" ] && [ -f "$ASKPASS_SCRIPT" ]; then
        rm -f "$ASKPASS_SCRIPT"
    fi
    unset MADTOM_DEPLOY_PASS
}
trap cleanup EXIT INT TERM

if command -v sshpass >/dev/null 2>&1; then
    USE_SSHPASS=1
else
    USE_SSHPASS=0
    # Setup temporary executable askpass script
    ASKPASS_SCRIPT=$(mktemp)
    chmod 700 "$ASKPASS_SCRIPT"
    cat << 'EOF' > "$ASKPASS_SCRIPT"
#!/usr/bin/env bash
echo "$MADTOM_DEPLOY_PASS"
EOF
    export MADTOM_DEPLOY_PASS="$SSH_PASS"
    export SSH_ASKPASS="$ASKPASS_SCRIPT"
    export SSH_ASKPASS_REQUIRE="force"
    export DISPLAY="${DISPLAY:-dummy:0}"
fi

run_ssh() {
    if [ "$USE_SSHPASS" -eq 1 ]; then
        sshpass -p "$SSH_PASS" ssh -p "$SSH_PORT" \
            -o StrictHostKeyChecking=accept-new \
            -o ConnectTimeout=10 \
            "${SSH_USER}@${SSH_HOST}" "$@"
    else
        ssh -p "$SSH_PORT" \
            -o StrictHostKeyChecking=accept-new \
            -o ConnectTimeout=10 \
            "${SSH_USER}@${SSH_HOST}" "$@"
    fi
}

run_scp() {
    local src="$1"
    local dst="$2"
    if [ "$USE_SSHPASS" -eq 1 ]; then
        sshpass -p "$SSH_PASS" scp -P "$SSH_PORT" \
            -o StrictHostKeyChecking=accept-new \
            -o ConnectTimeout=10 \
            "$src" "$dst"
    else
        scp -P "$SSH_PORT" \
            -o StrictHostKeyChecking=accept-new \
            -o ConnectTimeout=10 \
            "$src" "$dst"
    fi
}

# ------------------------------------------------------------------------------
# Remote Architecture Detection
# ------------------------------------------------------------------------------

if [ "$TARGET_ARCH" = "auto" ]; then
    echo "==> Probing remote architecture on ${SSH_USER}@${SSH_HOST}..."
    REMOTE_MACHINE="$(run_ssh uname -m 2>/dev/null || true)"
    REMOTE_MACHINE="$(echo "$REMOTE_MACHINE" | tr -d '\r\n[:space:]')"
    if [ -n "$REMOTE_MACHINE" ]; then
        case "$REMOTE_MACHINE" in
            x86_64|amd64)         TARGET_ARCH="amd64" ;;
            aarch64|arm64|armv8*) TARGET_ARCH="arm64" ;;
            armv7*|armv6*|armhf)  TARGET_ARCH="arm" ;;
            i386|i686)            TARGET_ARCH="386" ;;
            *)                    TARGET_ARCH="$DEFAULT_ARCH" ;;
        esac
        echo "    Detected remote architecture: $REMOTE_MACHINE (mapped to GOARCH=$TARGET_ARCH)"
    else
        echo "    Notice: Could not detect remote architecture via SSH. Defaulting to host architecture: $DEFAULT_ARCH"
        TARGET_ARCH="$DEFAULT_ARCH"
    fi
fi

# ------------------------------------------------------------------------------
# Binary Architecture Verification & Compilation
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

if [ -f "$LOCAL_BIN" ]; then
    BIN_ARCH="$(get_binary_arch "$LOCAL_BIN")"
    if [ "$BIN_ARCH" != "$TARGET_ARCH" ] && [ "$BIN_ARCH" != "unknown" ]; then
        echo "==> Local binary '$LOCAL_BIN' architecture ($BIN_ARCH) does not match target architecture ($TARGET_ARCH)."
        echo "    Recompiling Go binary for linux/$TARGET_ARCH..."
        DO_BUILD=1
    fi
fi

if [ "$DO_BUILD" -eq 1 ] || [ ! -f "$LOCAL_BIN" ]; then
    if [ ! -f "$LOCAL_BIN" ]; then
        echo "==> Local binary '$LOCAL_BIN' not found. Compiling for linux/$TARGET_ARCH..."
    else
        echo "==> Building Go executable for linux/$TARGET_ARCH (--build specified)..."
    fi

    # Determine which binary to build based on LOCAL_BIN name
    BIN_NAME="$(basename "$LOCAL_BIN")"
    CMD_DIR=""
    GO_SRC_DIR=""
    if [ -d "$SCRIPT_DIR/cmd/$BIN_NAME" ]; then
        GO_SRC_DIR="$SCRIPT_DIR"
        CMD_DIR="./cmd/$BIN_NAME"
    elif [ -d "$SCRIPT_DIR/MADTOM_GOLANG/cmd/$BIN_NAME" ]; then
        GO_SRC_DIR="$SCRIPT_DIR/MADTOM_GOLANG"
        CMD_DIR="./cmd/$BIN_NAME"
    elif [ -d "$SCRIPT_DIR/cmd/madtom-daemon" ]; then
        GO_SRC_DIR="$SCRIPT_DIR"
        CMD_DIR="./cmd/madtom-daemon"
    elif [ -d "$SCRIPT_DIR/MADTOM_GOLANG/cmd/madtom-daemon" ]; then
        GO_SRC_DIR="$SCRIPT_DIR/MADTOM_GOLANG"
        CMD_DIR="./cmd/madtom-daemon"
    elif [ -d "$SCRIPT_DIR/cmd/madtom-collector" ]; then
        GO_SRC_DIR="$SCRIPT_DIR"
        CMD_DIR="./cmd/madtom-collector"
    elif [ -d "$SCRIPT_DIR/MADTOM_GOLANG/cmd/madtom-collector" ]; then
        GO_SRC_DIR="$SCRIPT_DIR/MADTOM_GOLANG"
        CMD_DIR="./cmd/madtom-collector"
    fi

    if [ -n "$CMD_DIR" ] && [ -n "$GO_SRC_DIR" ]; then
        mkdir -p "$(dirname "$LOCAL_BIN")"
        GOARM_ARG=()
        if [ "$TARGET_ARCH" = "arm" ]; then
            GOARM_ARG=("GOARM=7")
        fi
        (cd "$GO_SRC_DIR" && env CGO_ENABLED=0 GOOS=linux GOARCH="$TARGET_ARCH" "${GOARM_ARG[@]}" go build -buildvcs=false -ldflags="-s -w" -o "$LOCAL_BIN" "$CMD_DIR")
        
        # Also store arch-specific binary in bin/linux_${TARGET_ARCH}/
        mkdir -p "$SCRIPT_DIR/bin/linux_${TARGET_ARCH}"
        cp -f "$LOCAL_BIN" "$SCRIPT_DIR/bin/linux_${TARGET_ARCH}/$BIN_NAME" 2>/dev/null || true
        
        echo "==> Built: $LOCAL_BIN (Target: linux/$TARGET_ARCH)"
    else
        echo "Error: Cannot find Go source to build $LOCAL_BIN. Please compile it first." >&2
        exit 1
    fi
fi

if [ ! -f "$LOCAL_BIN" ]; then
    echo "Error: Local binary '$LOCAL_BIN' not found." >&2
    exit 1
fi

BIN_NAME="$(basename "$LOCAL_BIN")"
if [ "$(basename "$DEST_PATH")" != "$BIN_NAME" ]; then
    TARGET_DIR="$DEST_PATH"
    TARGET_BIN="$DEST_PATH/$BIN_NAME"
else
    TARGET_DIR="$(dirname "$DEST_PATH")"
    TARGET_BIN="$DEST_PATH"
fi

BIN_SIZE="$(du -h "$LOCAL_BIN" | cut -f1)"
echo "----------------------------------------------------------------"
echo "MADTOM Backend Deployment"
echo "----------------------------------------------------------------"
echo "  Target Host  : ${SSH_USER}@${SSH_HOST}:${SSH_PORT}"
echo "  Architecture : linux/${TARGET_ARCH}"
echo "  Local Binary : $LOCAL_BIN ($BIN_SIZE)"
echo "  Destination  : $TARGET_BIN"
echo "  Service Name : $SERVICE_NAME"
echo "----------------------------------------------------------------"

# ------------------------------------------------------------------------------
# Step 1: Upload Executable to Remote Temp Directory
# ------------------------------------------------------------------------------

REMOTE_TMP="${REMOTE_TMP_DIR}/madtom_deploy_$(date +%s)_$RANDOM"
echo "==> [1/3] Uploading binary to remote staging path: $REMOTE_TMP ..."
run_scp "$LOCAL_BIN" "${SSH_USER}@${SSH_HOST}:${REMOTE_TMP}"

# ------------------------------------------------------------------------------
# Step 2: Install Executable via Sudo & Step 3: Restart Service
# ------------------------------------------------------------------------------

echo "==> [2/3] Installing binary to $TARGET_BIN with sudo ..."
echo "==> [3/3] Restarting systemd service: $SERVICE_NAME ..."

# The remote script reads the password from standard input so it is never
# exposed in ps or /proc command line arguments on the remote host.
REMOTE_SCRIPT=$(cat << EOF
set -euo pipefail

# Read password from stdin
IFS= read -r SUDO_PASS

run_sudo() {
    printf '%s\n' "\$SUDO_PASS" | sudo -S -p '' "\$@"
}

TARGET_DIR="$TARGET_DIR"
TARGET_BIN="$TARGET_BIN"

# If TARGET_DIR exists as a regular file (from a previous erroneous deployment), remove it
if [ -f "\$TARGET_DIR" ]; then
    echo "    Cleaning up existing regular file at directory path: \$TARGET_DIR"
    run_sudo rm -f "\$TARGET_DIR"
fi

if [ ! -d "\$TARGET_DIR" ]; then
    echo "    Creating destination directory: \$TARGET_DIR"
    run_sudo mkdir -p "\$TARGET_DIR"
fi

echo "    Copying binary from $REMOTE_TMP to \$TARGET_BIN (sudo)"
run_sudo cp -f "$REMOTE_TMP" "\${TARGET_BIN}.new"
run_sudo chmod 755 "\${TARGET_BIN}.new"
run_sudo chown root:root "\${TARGET_BIN}.new" 2>/dev/null || true
run_sudo mv -f "\${TARGET_BIN}.new" "\$TARGET_BIN"

# Cleanup staging file
rm -f "$REMOTE_TMP"

# Systemd operations
if command -v systemctl >/dev/null 2>&1; then
    # Inspect service definition to ensure binary path matches ExecStart
    SERVICE_EXEC="\$(run_sudo systemctl cat "$SERVICE_NAME" 2>/dev/null | grep -E '^\s*ExecStart=' | head -n1 | awk '{print \$1}' | sed 's/^\s*ExecStart=//' || true)"
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
    echo "    Restarting service: $SERVICE_NAME"
    run_sudo systemctl restart "$SERVICE_NAME"

    echo "    Verifying service status..."
    if run_sudo systemctl is-active --quiet "$SERVICE_NAME"; then
        echo "    Status: ACTIVE (Running)"
    else
        echo "    Warning: Service '$SERVICE_NAME' is not reported as active. Service log summary:"
        run_sudo journalctl -u "$SERVICE_NAME" -n 15 --no-pager || true
        exit 1
    fi
else
    echo "    Notice: systemctl not detected on remote host. Binary updated successfully."
fi
EOF
)

# Stream password into remote script over SSH
printf '%s\n' "$SSH_PASS" | run_ssh "bash -c $(printf %q "$REMOTE_SCRIPT")"

echo "----------------------------------------------------------------"
echo " Deployment completed successfully!"
echo "   Host    : ${SSH_USER}@${SSH_HOST}"
echo "   Binary  : $TARGET_BIN"
echo "   Service : $SERVICE_NAME (Restarted)"
echo "----------------------------------------------------------------"

