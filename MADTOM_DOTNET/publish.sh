#!/usr/bin/env bash
# ==============================================================================
# MADTOM .NET Self-Contained Linux Publishing Script
# Publishes .NET projects as self-contained Linux binaries to:
#   MADTOM_DOTNET/publish/{OS_arch}
# ==============================================================================
set -euo pipefail

# Resolve real script directory even if invoked via symlink
REAL_SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
SCRIPT_DIR="$(cd "$(dirname "$REAL_SCRIPT_PATH")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PUBLISH_BASE="$SCRIPT_DIR/publish"

# Styling
BOLD='\033[1m'
CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Detect host default RID
HOST_UNAME="$(uname -m)"
case "$HOST_UNAME" in
    x86_64)   DEFAULT_RID="linux-x64" ;;
    aarch64)  DEFAULT_RID="linux-arm64" ;;
    armv7*|armhf) DEFAULT_RID="linux-arm" ;;
    *)        DEFAULT_RID="linux-x64" ;;
esac

TARGET_RID="$DEFAULT_RID"
PROJECT_SEL="all" # all, console, telemetry-app
CONFIG="Release"
SINGLE_FILE=true
CLEAN=false

usage() {
    cat << EOU | while IFS= read -r line; do echo -e "$line"; done
${BOLD}Usage:${NC} $(basename "$0") [OPTIONS]

Publish self-contained .NET Linux executables for MADTOM.
Output target: ${BOLD}MADTOM_DOTNET/publish/{OS_arch}${NC}

${BOLD}Options:${NC}
  -a, --arch, -r, --rid RID   Target Linux architecture/RID:
                              - linux-x64   (aliases: x64, amd64)
                              - linux-arm64 (aliases: arm64, aarch64)
                              - linux-arm   (aliases: arm, armv7)
                              - all         (build all supported Linux architectures)
                              (default: $DEFAULT_RID)

  -p, --project NAME          Project to publish:
                              - studio          (MADTOM.Studio - Avalonia Desktop App)
                              - console         (Alias for studio)
                              - telemetry-app   (MADTOM.Plugins.Telemetry.App)
                              - squeeze-app     (MADTOM.Plugins.Squeeze.App)
                              - android-app     (MADTOM.Plugins.AndroidToolkit.App)
                              - mediacenter-app (MADTOM.Plugins.MediaCenter.App)
                              - noxai-app       (MADTOM.Plugins.NoxAI.App)
                              - audiosync-app   (MADTOM.Plugins.AudioSync.App)
                              - vna-app         (MADTOM.Plugins.VNA.App)
                              - connection-app  (MADTOM.Plugins.ConnectionToolkit.App)
                              - all             (all runnable projects, default)

  -c, --config CONFIG         Build configuration: Release | Debug (default: Release)
  --single-file               Package into self-contained single-file executable (default)
  --no-single-file            Publish as loose directory of assemblies & native libraries
  --clean                     Remove previous publish artifacts before building
  -h, --help                  Display this help message

${BOLD}Examples:${NC}
  $(basename "$0")
  $(basename "$0") --arch arm64
  $(basename "$0") --arch all
  $(basename "$0") -p console -a linux-arm64
EOU
    exit 0
}

# Parse CLI arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        -a|--arch|-r|--rid)
            case "${2,,}" in
                x64|amd64|linux-x64)       TARGET_RID="linux-x64" ;;
                arm64|aarch64|linux-arm64) TARGET_RID="linux-arm64" ;;
                arm|armv7|armhf|linux-arm) TARGET_RID="linux-arm" ;;
                all)                       TARGET_RID="all" ;;
                *)                         TARGET_RID="$2" ;;
            esac
            shift 2
            ;;
        -p|--project)
            PROJECT_SEL="${2,,}"
            shift 2
            ;;
        -c|--config)
            CONFIG="$2"
            shift 2
            ;;
        --single-file)
            SINGLE_FILE=true
            shift
            ;;
        --no-single-file)
            SINGLE_FILE=false
            shift
            ;;
        --clean)
            CLEAN=true
            shift
            ;;
        -h|--help)
            usage
            ;;
        *)
            echo -e "${RED}Unknown option: $1${NC}" >&2
            usage
            ;;
    esac
done

# Verify dotnet CLI
if ! command -v dotnet >/dev/null 2>&1; then
    echo -e "${RED}Error: 'dotnet' is not installed or not in PATH.${NC}" >&2
    exit 1
fi

# Define projects
declare -A PROJECTS
PROJECTS["MADTOM.Studio"]="$SCRIPT_DIR/src/Host/MADTOM.Studio/MADTOM.Studio.csproj"
PROJECTS["MADTOM.Plugins.Telemetry.App"]="$SCRIPT_DIR/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry.App/MADTOM.Plugins.Telemetry.App.csproj"
PROJECTS["MADTOM.Plugins.Squeeze.App"]="$SCRIPT_DIR/src/Plugins/Squeeze/MADTOM.Plugins.Squeeze.App/MADTOM.Plugins.Squeeze.App.csproj"
PROJECTS["MADTOM.Plugins.AndroidToolkit.App"]="$SCRIPT_DIR/src/Plugins/AndroidToolkit/MADTOM.Plugins.AndroidToolkit.App/MADTOM.Plugins.AndroidToolkit.App.csproj"
PROJECTS["MADTOM.Plugins.MediaCenter.App"]="$SCRIPT_DIR/src/Plugins/MediaCenter/MADTOM.Plugins.MediaCenter.App/MADTOM.Plugins.MediaCenter.App.csproj"
PROJECTS["MADTOM.Plugins.NoxAI.App"]="$SCRIPT_DIR/src/Plugins/NoxAI/MADTOM.Plugins.NoxAI.App/MADTOM.Plugins.NoxAI.App.csproj"
PROJECTS["MADTOM.Plugins.AudioSync.App"]="$SCRIPT_DIR/src/Plugins/AudioSync/MADTOM.Plugins.AudioSync.App/MADTOM.Plugins.AudioSync.App.csproj"
PROJECTS["MADTOM.Plugins.VNA.App"]="$SCRIPT_DIR/src/Plugins/VNA/MADTOM.Plugins.VNA.App/MADTOM.Plugins.VNA.App.csproj"
PROJECTS["MADTOM.Plugins.ConnectionToolkit.App"]="$SCRIPT_DIR/src/Plugins/ConnectionToolkit/MADTOM.Plugins.ConnectionToolkit.App/MADTOM.Plugins.ConnectionToolkit.App.csproj"

# Filter projects to build
declare -A SELECTED_PROJECTS
case "$PROJECT_SEL" in
    studio|console)
        SELECTED_PROJECTS["MADTOM.Studio"]="${PROJECTS["MADTOM.Studio"]}"
        ;;
    telemetry-app)
        SELECTED_PROJECTS["MADTOM.Plugins.Telemetry.App"]="${PROJECTS["MADTOM.Plugins.Telemetry.App"]}"
        ;;
    squeeze-app)
        SELECTED_PROJECTS["MADTOM.Plugins.Squeeze.App"]="${PROJECTS["MADTOM.Plugins.Squeeze.App"]}"
        ;;
    android-app)
        SELECTED_PROJECTS["MADTOM.Plugins.AndroidToolkit.App"]="${PROJECTS["MADTOM.Plugins.AndroidToolkit.App"]}"
        ;;
    mediacenter-app)
        SELECTED_PROJECTS["MADTOM.Plugins.MediaCenter.App"]="${PROJECTS["MADTOM.Plugins.MediaCenter.App"]}"
        ;;
    noxai-app)
        SELECTED_PROJECTS["MADTOM.Plugins.NoxAI.App"]="${PROJECTS["MADTOM.Plugins.NoxAI.App"]}"
        ;;
    audiosync-app)
        SELECTED_PROJECTS["MADTOM.Plugins.AudioSync.App"]="${PROJECTS["MADTOM.Plugins.AudioSync.App"]}"
        ;;
    vna-app)
        SELECTED_PROJECTS["MADTOM.Plugins.VNA.App"]="${PROJECTS["MADTOM.Plugins.VNA.App"]}"
        ;;
    connection-app)
        SELECTED_PROJECTS["MADTOM.Plugins.ConnectionToolkit.App"]="${PROJECTS["MADTOM.Plugins.ConnectionToolkit.App"]}"
        ;;
    all)
        for p in "${!PROJECTS[@]}"; do
            SELECTED_PROJECTS["$p"]="${PROJECTS["$p"]}"
        done
        ;;
    *)
        echo -e "${RED}Error: Unknown project '$PROJECT_SEL'. Choose 'studio', 'telemetry-app', 'squeeze-app', 'android-app', 'mediacenter-app', 'noxai-app', 'audiosync-app', 'vna-app', 'connection-app', or 'all'.${NC}" >&2
        exit 1
        ;;
esac

# Resolve target RIDs
if [ "$TARGET_RID" = "all" ]; then
    RIDS=("linux-x64" "linux-arm64" "linux-arm")
else
    RIDS=("$TARGET_RID")
fi

echo -e "${BOLD}${CYAN}================================================================${NC}"
echo -e "${BOLD}${CYAN} MADTOM .NET Linux Self-Contained Publishing${NC}"
echo -e "${BOLD}${CYAN}================================================================${NC}"
echo -e "  Configuration   : ${BOLD}${CONFIG}${NC}"
echo -e "  Target RIDs     : ${BOLD}${RIDS[*]}${NC}"
echo -e "  Projects        : ${BOLD}${!SELECTED_PROJECTS[*]}${NC}"
echo -e "  Single File     : ${BOLD}${SINGLE_FILE}${NC}"
echo -e "  Base Output Dir : ${BOLD}${PUBLISH_BASE}/{OS_arch}${NC}"
echo -e "${BOLD}${CYAN}================================================================${NC}"
echo ""

# Clean if requested
if [ "$CLEAN" = true ]; then
    echo -e "${YELLOW}Cleaning publish directory: $PUBLISH_BASE ...${NC}"
    rm -rf "$PUBLISH_BASE"
    echo ""
fi

# Build flags
COMMON_FLAGS=(
    "-c" "$CONFIG"
    "--self-contained" "true"
    "-p:IncludeNativeLibrariesForSelfExtract=true"
    "-p:DebugType=none"
    "-p:DebugSymbols=false"
    "--nologo"
    "-v" "minimal"
)

if [ "$SINGLE_FILE" = true ]; then
    COMMON_FLAGS+=("-p:PublishSingleFile=true")
fi

TOTAL_START=$(date +%s)
SUCCESS_COUNT=0

for rid in "${RIDS[@]}"; do
    ARCH_OUTPUT_DIR="$PUBLISH_BASE/$rid"
    echo -e "${BOLD}${GREEN}==> Target Architecture: ${rid}${NC}"
    echo -e "    Destination: ${ARCH_OUTPUT_DIR}"

    for proj_name in "${!SELECTED_PROJECTS[@]}"; do
        proj_file="${SELECTED_PROJECTS[$proj_name]}"
        PROJ_OUTPUT="$ARCH_OUTPUT_DIR/$proj_name"

        echo -e "    -> Publishing ${BOLD}${proj_name}${NC} (${rid}) ..."
        mkdir -p "$PROJ_OUTPUT"

        dotnet publish "$proj_file" \
            "${COMMON_FLAGS[@]}" \
            "-r" "$rid" \
            "-o" "$PROJ_OUTPUT"

        # Make the main executable runnable
        if [ -f "$PROJ_OUTPUT/$proj_name" ]; then
            chmod 755 "$PROJ_OUTPUT/$proj_name"
            BIN_SIZE="$(du -h "$PROJ_OUTPUT/$proj_name" | cut -f1)"
            echo -e "       ${GREEN}✓ Executable: ${PROJ_OUTPUT}/${proj_name} (${BIN_SIZE})${NC}"
        fi

        # Also copy direct convenience binary to {OS_arch}/ if single project
        if [ "${#SELECTED_PROJECTS[@]}" -eq 1 ] && [ -f "$PROJ_OUTPUT/$proj_name" ]; then
            cp -f "$PROJ_OUTPUT/$proj_name" "$ARCH_OUTPUT_DIR/$proj_name" 2>/dev/null || true
        fi

        SUCCESS_COUNT=$((SUCCESS_COUNT + 1))
    done
    echo ""
done

TOTAL_TIME=$(( $(date +%s) - TOTAL_START ))
echo -e "${BOLD}${CYAN}================================================================${NC}"
echo -e "${BOLD}${GREEN} Publishing Complete! ($SUCCESS_COUNT builds succeeded in ${TOTAL_TIME}s)${NC}"
echo -e "${BOLD}${CYAN}================================================================${NC}"
echo -e "Output Directory Summary:"
for rid in "${RIDS[@]}"; do
    echo -e "  ${BOLD}${PUBLISH_BASE}/${rid}/${NC}"
    if [ -d "$PUBLISH_BASE/$rid" ]; then
        find "$PUBLISH_BASE/$rid" -maxdepth 2 -type f -executable 2>/dev/null | while read -r exec_bin; do
            echo -e "    - $(basename "$(dirname "$exec_bin")")/$(basename "$exec_bin") ($(du -h "$exec_bin" | cut -f1))"
        done
    fi
done
echo ""
echo -e "To execute on a target Linux machine:"
echo -e "  chmod +x <binary>"
echo -e "  ./<binary>"
echo -e "${BOLD}${CYAN}================================================================${NC}"
