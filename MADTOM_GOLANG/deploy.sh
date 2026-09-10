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
  --build               Build the Go binary locally before deploying
  --help                Show this help message and exit

Examples:
  $(basename "$0") root@192.168.1.100
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
# Optional Build Step or Binary Verification
# ------------------------------------------------------------------------------

if [ "$DO_BUILD" -eq 1 ] || [ ! -f "$LOCAL_BIN" ]; then
    if [ ! -f "$LOCAL_BIN" ]; then
        echo "==> Local binary '$LOCAL_BIN' not found. Compiling Go executable..."
    else
        echo "==> Building Go executable (--build specified)..."
    fi

    # Determine which binary to build based on LOCAL_BIN name
    BIN_NAME="$(basename "$LOCAL_BIN")"
    CMD_DIR=""
    if [ -d "$SCRIPT_DIR/cmd/$BIN_NAME" ]; then
        CMD_DIR="./cmd/$BIN_NAME"
    elif [ -d "$SCRIPT_DIR/cmd/madtom-daemon" ]; then
        CMD_DIR="./cmd/madtom-daemon"
    elif [ -d "$SCRIPT_DIR/cmd/madtom-collector" ]; then
        CMD_DIR="./cmd/madtom-collector"
    fi

    if [ -n "$CMD_DIR" ]; then
        mkdir -p "$(dirname "$LOCAL_BIN")"
        (cd "$SCRIPT_DIR" && CGO_ENABLED=0 GOOS=linux go build -ldflags="-s -w" -o "$LOCAL_BIN" "$CMD_DIR")
        echo "==> Built: $LOCAL_BIN"
    else
        echo "Error: Cannot find Go source to build $LOCAL_BIN. Please compile it first." >&2
        exit 1
    fi
fi

if [ ! -f "$LOCAL_BIN" ]; then
    echo "Error: Local binary '$LOCAL_BIN' not found." >&2
    exit 1
fi

BIN_SIZE="$(du -h "$LOCAL_BIN" | cut -f1)"
echo "----------------------------------------------------------------"
echo "MADTOM Backend Deployment"
echo "----------------------------------------------------------------"
echo "  Target Host  : ${SSH_USER}@${SSH_HOST}:${SSH_PORT}"
echo "  Local Binary : $LOCAL_BIN ($BIN_SIZE)"
echo "  Destination  : $DEST_PATH"
echo "  Service Name : $SERVICE_NAME"
echo "----------------------------------------------------------------"

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
# Step 1: Upload Executable to Remote Temp Directory
# ------------------------------------------------------------------------------

REMOTE_TMP="${REMOTE_TMP_DIR}/madtom_deploy_$(date +%s)_$RANDOM"
echo "==> [1/3] Uploading binary to remote staging path: $REMOTE_TMP ..."
run_scp "$LOCAL_BIN" "${SSH_USER}@${SSH_HOST}:${REMOTE_TMP}"

# ------------------------------------------------------------------------------
# Step 2: Install Executable via Sudo & Step 3: Restart Service
# ------------------------------------------------------------------------------

echo "==> [2/3] Installing binary to $DEST_PATH with sudo ..."
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

DEST_DIR="\$(dirname "$DEST_PATH")"
if [ ! -d "\$DEST_DIR" ]; then
    echo "    Creating destination directory: \$DEST_DIR"
    run_sudo mkdir -p "\$DEST_DIR"
fi

echo "    Copying binary from $REMOTE_TMP to $DEST_PATH (sudo)"
run_sudo cp "$REMOTE_TMP" "$DEST_PATH"
run_sudo chmod 755 "$DEST_PATH"
run_sudo chown root:root "$DEST_PATH" 2>/dev/null || true

# Cleanup staging file
rm -f "$REMOTE_TMP"

# Systemd operations
if command -v systemctl >/dev/null 2>&1; then
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
echo "   Binary  : $DEST_PATH"
echo "   Service : $SERVICE_NAME (Restarted)"
echo "----------------------------------------------------------------"

