#!/usr/bin/env bash
# ==============================================================================
# MADTOM Go Daemons Build Script
# Compiles Go backend services (madtom-daemon, madtom-collector) for Linux
# architectures including amd64, arm64 (aarch64), and arm (ARMv7).
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="$SCRIPT_DIR/bin"

# Styling
BOLD='\033[1m'
CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Detect host default architecture
HOST_UNAME="$(uname -m)"
case "$HOST_UNAME" in
    x86_64)               DEFAULT_ARCH="amd64" ;;
    aarch64|arm64|armv8*) DEFAULT_ARCH="arm64" ;;
    armv7*|armv6*|armhf)  DEFAULT_ARCH="arm" ;;
    *)                    DEFAULT_ARCH="amd64" ;;
esac

TARGET_ARCH="$DEFAULT_ARCH"
TARGET_PKG="all" # all, daemon, collector
CONFIG="Release"
CLEAN=false

usage() {
    cat << EOF
${BOLD}Usage:${NC} $(basename "$0") [OPTIONS]

Compile MADTOM Go daemons with multi-architecture support.

${BOLD}Options:${NC}
  -a, --arch ARCH       Target architecture:
                          - amd64     (x86_64)
                          - arm64     (ARM 64-bit / aarch64)
                          - arm       (ARM 32-bit v7 / armhf)
                          - all       (build for amd64, arm64, and arm)
                        (default: $DEFAULT_ARCH)

  -p, --package PKG     Package to build:
                          - daemon    (madtom-daemon only)
                          - collector (madtom-collector only)
                          - all       (both daemons, default)

  -c, --clean           Clean bin directory before building
  --release             Build optimized release binaries with stripped symbols (default)
  --debug               Build with debug symbols
  -h, --help            Show this help message and exit

${BOLD}Examples:${NC}
  $(basename "$0")
  $(basename "$0") --arch arm64
  $(basename "$0") --arch all
  $(basename "$0") -p daemon -a arm64
EOF
    exit 0
}

# Parse CLI arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        -a|--arch)
            case "${2,,}" in
                amd64|x86_64|x64)          TARGET_ARCH="amd64" ;;
                arm64|aarch64|linux-arm64) TARGET_ARCH="arm64" ;;
                arm|armv7|armhf|linux-arm) TARGET_ARCH="arm" ;;
                all)                       TARGET_ARCH="all" ;;
                *)                         TARGET_ARCH="$2" ;;
            esac
            shift 2
            ;;
        -p|--package|--pkg)
            case "${2,,}" in
                daemon|madtom-daemon)       TARGET_PKG="daemon" ;;
                collector|madtom-collector) TARGET_PKG="collector" ;;
                all)                        TARGET_PKG="all" ;;
                *)
                    echo -e "${RED}Error: Unknown package '$2'. Choose 'daemon', 'collector', or 'all'.${NC}" >&2
                    exit 1
                    ;;
            esac
            shift 2
            ;;
        -c|--clean)
            CLEAN=true
            shift
            ;;
        --release)
            CONFIG="Release"
            shift
            ;;
        --debug)
            CONFIG="Debug"
            shift
            ;;
        -h|--help)
            usage
            ;;
        *)
            echo -e "${RED}Unknown argument: $1${NC}" >&2
            usage
            ;;
    esac
done

# Verify Go toolchain
if ! command -v go >/dev/null 2>&1; then
    echo -e "${RED}Error: 'go' is not installed or not in PATH.${NC}" >&2
    exit 1
fi

# Clean if requested
if [ "$CLEAN" = true ]; then
    echo -e "${YELLOW}Cleaning $BIN_DIR ...${NC}"
    rm -rf "$BIN_DIR"
fi

mkdir -p "$BIN_DIR"

# Resolve target architectures
if [ "$TARGET_ARCH" = "all" ]; then
    ARCHES=("amd64" "arm64" "arm")
else
    ARCHES=("$TARGET_ARCH")
fi

# Resolve targets
PKGS=()
case "$TARGET_PKG" in
    daemon)    PKGS=("madtom-daemon") ;;
    collector) PKGS=("madtom-collector") ;;
    all)       PKGS=("madtom-collector" "madtom-daemon") ;;
esac

echo -e "${BOLD}${CYAN}================================================================${NC}"
echo -e "${BOLD}${CYAN} MADTOM Go Backend Daemon Compiler${NC}"
echo -e "${BOLD}${CYAN}================================================================${NC}"
echo -e "  Configuration : ${BOLD}${CONFIG}${NC}"
echo -e "  Architectures : ${BOLD}${ARCHES[*]}${NC}"
echo -e "  Packages      : ${BOLD}${PKGS[*]}${NC}"
echo -e "  Output Base   : ${BOLD}${BIN_DIR}${NC}"
echo -e "${BOLD}${CYAN}================================================================${NC}"
echo ""

START_TIME=$(date +%s)
TOTAL_BUILT=0

for arch in "${ARCHES[@]}"; do
    ARCH_DIR="$BIN_DIR/linux_${arch}"
    mkdir -p "$ARCH_DIR"

    GOARM_ENV=()
    if [ "$arch" = "arm" ]; then
        GOARM_ENV=("GOARM=7")
    fi

    GO_FLAGS=("-buildvcs=false")
    if [ "$CONFIG" = "Release" ]; then
        GO_FLAGS+=("-ldflags=-s -w")
    fi

    echo -e "${BOLD}${GREEN}==> Architecture: linux/${arch}${NC}"

    for pkg in "${PKGS[@]}"; do
        OUT_PATH="$ARCH_DIR/$pkg"
        echo -e "    -> Building ${BOLD}${pkg}${NC} (linux/${arch}) ..."

        (
            cd "$SCRIPT_DIR"
            env CGO_ENABLED=0 GOOS=linux GOARCH="$arch" "${GOARM_ENV[@]}" \
                go build "${GO_FLAGS[@]}" -o "$OUT_PATH" "./cmd/$pkg"
        )

        BIN_SIZE="$(du -h "$OUT_PATH" | cut -f1)"
        echo -e "       ${GREEN}✓ ${OUT_PATH} (${BIN_SIZE})${NC}"

        # If this matches host architecture, also copy to top-level bin/ for convenience
        if [ "$arch" = "$DEFAULT_ARCH" ]; then
            cp -f "$OUT_PATH" "$BIN_DIR/$pkg" 2>/dev/null || true
        fi

        TOTAL_BUILT=$((TOTAL_BUILT + 1))
    done
    echo ""
done

ELAPSED=$(( $(date +%s) - START_TIME ))
echo -e "${BOLD}${CYAN}================================================================${NC}"
echo -e "${BOLD}${GREEN} Build Successful! ($TOTAL_BUILT binaries compiled in ${ELAPSED}s)${NC}"
echo -e "${BOLD}${CYAN}================================================================${NC}"
echo "Summary of compiled binaries:"
for arch in "${ARCHES[@]}"; do
    echo -e "  ${BOLD}linux/${arch}:${NC}"
    for pkg in "${PKGS[@]}"; do
        bin_file="$BIN_DIR/linux_${arch}/$pkg"
        if [ -f "$bin_file" ]; then
            arch_info="$(file -b "$bin_file" | cut -d',' -f1,2)"
            echo -e "    - $pkg ($(du -h "$bin_file" | cut -f1)) [$arch_info]"
        fi
    done
done
echo -e "${BOLD}${CYAN}================================================================${NC}"

