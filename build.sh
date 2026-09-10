#!/usr/bin/env bash
# ==============================================================================
# MADTOM Unified Build Script
# Compiles both Go backend daemons and .NET C# solution.
# ==============================================================================
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GO_DIR="$ROOT_DIR/MADTOM_GOLANG"
DOTNET_DIR="$ROOT_DIR/MADTOM_DOTNET"

# Styling
BOLD='\033[1m'
CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

CONFIG="Debug"
RUN_TESTS=false
CLEAN_BUILD=false

usage() {
    echo -e "${BOLD}Usage:${NC} $0 [OPTIONS]"
    echo ""
    echo -e "${BOLD}Options:${NC}"
    echo "  --release       Build both Go and .NET projects with Release optimizations"
    echo "  --debug         Build projects in Debug configuration (default)"
    echo "  --test          Run automated unit tests after compiling"
    echo "  --clean         Clean previous build artifacts before compiling"
    echo "  -h, --help      Display this help message"
    exit 0
}

# Parse CLI arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --release)
            CONFIG="Release"
            shift
            ;;
        --debug)
            CONFIG="Debug"
            shift
            ;;
        --test)
            RUN_TESTS=true
            shift
            ;;
        --clean)
            CLEAN_BUILD=true
            shift
            ;;
        -h|--help)
            usage
            ;;
        *)
            echo -e "${RED}Unknown option: $1${NC}"
            usage
            ;;
    esac
done

echo -e "${BOLD}${CYAN}=== MADTOM Project Build ===${NC}"
echo -e "Configuration : ${BOLD}${CONFIG}${NC}"
echo -e "Root Directory: ${ROOT_DIR}"
echo ""

# Verify prerequisites
if ! command -v go >/dev/null 2>&1; then
    echo -e "${RED}Error: 'go' is not installed or not in PATH.${NC}" >&2
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo -e "${RED}Error: 'dotnet' is not installed or not in PATH.${NC}" >&2
    exit 1
fi

# ------------------------------------------------------------------------------
# 1. Clean (Optional)
# ------------------------------------------------------------------------------
if [[ "$CLEAN_BUILD" == true ]]; then
    echo -e "${YELLOW}Cleaning previous build artifacts...${NC}"
    rm -rf "$GO_DIR/bin"
    dotnet clean "$DOTNET_DIR/MADTOM.sln" -v minimal -c "$CONFIG" || true
    echo ""
fi

# ------------------------------------------------------------------------------
# 2. Compile Go Projects (madtom-collector and madtom-daemon)
# ------------------------------------------------------------------------------
echo -e "${BOLD}${CYAN}[1/2] Compiling Go Services (MADTOM_GOLANG)...${NC}"
mkdir -p "$GO_DIR/bin"

GO_FLAGS=("-buildvcs=false")
if [[ "$CONFIG" == "Release" ]]; then
    GO_FLAGS+=("-ldflags=-s -w")
fi

echo -e "  -> Building ${BOLD}madtom-collector${NC}..."
(cd "$GO_DIR" && go build "${GO_FLAGS[@]}" -o "$GO_DIR/bin/madtom-collector" ./cmd/madtom-collector)

echo -e "  -> Building ${BOLD}madtom-daemon${NC}..."
(cd "$GO_DIR" && go build "${GO_FLAGS[@]}" -o "$GO_DIR/bin/madtom-daemon" ./cmd/madtom-daemon)

echo -e "${GREEN}✓ Go binaries built successfully in ${GO_DIR}/bin/${NC}"
echo ""

# ------------------------------------------------------------------------------
# 3. Compile C# / .NET Solution (MADTOM_DOTNET)
# ------------------------------------------------------------------------------
echo -e "${BOLD}${CYAN}[2/2] Compiling .NET Solution (MADTOM_DOTNET)...${NC}"
dotnet build "$DOTNET_DIR/MADTOM.sln" -c "$CONFIG" --nologo -v minimal

echo -e "${GREEN}✓ .NET solution compiled successfully (${CONFIG}).${NC}"
echo ""

# ------------------------------------------------------------------------------
# 4. Optional Tests
# ------------------------------------------------------------------------------
if [[ "$RUN_TESTS" == true ]]; then
    echo -e "${BOLD}${CYAN}[Tests] Running Go test suite...${NC}"
    (cd "$GO_DIR" && go test ./...)
    echo -e "${GREEN}✓ Go tests passed.${NC}"
    echo ""

    echo -e "${BOLD}${CYAN}[Tests] Running .NET test suite...${NC}"
    dotnet test "$DOTNET_DIR/MadTOM.Tests/MadTOM.Tests.csproj" -c "$CONFIG" --nologo -v minimal
    echo -e "${GREEN}✓ .NET tests passed.${NC}"
    echo ""
fi

echo -e "${BOLD}${GREEN}=== All MADTOM components built successfully! ===${NC}"
