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
TEST_PLUGIN=""
CLEAN_BUILD=false
ARCH_ARG=""

usage() {
    echo -e "${BOLD}Usage:${NC} $0 [OPTIONS]"
    echo ""
    echo -e "${BOLD}Options:${NC}"
    echo "  -a, --arch ARCH        Target architecture for Go backend:"
    echo "                         - amd64   (x86_64)"
    echo "                         - arm64   (ARM 64-bit / aarch64)"
    echo "                         - arm     (ARM 32-bit v7 / armhf)"
    echo "                         - all     (build all architectures)"
    echo "                         (default: host architecture)"
    echo "  --release              Build both Go and .NET projects with Release optimizations"
    echo "  --debug                Build projects in Debug configuration (default)"
    echo "  --test                 Run all automated unit tests after compiling"
    echo "  --test-plugin NAME     Run targeted tests for a plugin (telemetry, squeeze, android, mediacenter, noxai, audiosync, console)"
    echo "  --clean                Clean previous build artifacts before compiling"
    echo "  -h, --help             Display this help message"
    exit 0
}

# Parse CLI arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        -a|--arch)
            ARCH_ARG="$2"
            shift 2
            ;;
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
        --test-plugin)
            RUN_TESTS=true
            TEST_PLUGIN="${2,,}"
            shift 2
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
if [ -n "$ARCH_ARG" ]; then
    echo -e "Go Architecture: ${BOLD}${ARCH_ARG}${NC}"
fi
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
GO_BUILD_CMD=("$GO_DIR/build.sh")
if [ "$CONFIG" == "Release" ]; then
    GO_BUILD_CMD+=("--release")
else
    GO_BUILD_CMD+=("--debug")
fi
if [ -n "$ARCH_ARG" ]; then
    GO_BUILD_CMD+=("-a" "$ARCH_ARG")
fi

"${GO_BUILD_CMD[@]}"
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
    # Targeted or full Go tests
    if [ -z "$TEST_PLUGIN" ]; then
        echo -e "${BOLD}${CYAN}[Tests] Running full Go test suite...${NC}"
        (cd "$GO_DIR" && go test ./...)
        echo -e "${GREEN}✓ Go tests passed.${NC}"
        echo ""
    elif [ "$TEST_PLUGIN" = "telemetry" ]; then
        echo -e "${BOLD}${CYAN}[Tests] Running targeted Go tests for Telemetry (./pkg/telemetry/...)...${NC}"
        (cd "$GO_DIR" && go test ./pkg/telemetry/...)
        echo -e "${GREEN}✓ Telemetry Go tests passed.${NC}"
        echo ""
    elif [ "$TEST_PLUGIN" = "squeeze" ]; then
        echo -e "${BOLD}${CYAN}[Tests] Running targeted Go tests for Squeeze (./pkg/squeeze/...)...${NC}"
        (cd "$GO_DIR" && go test ./pkg/squeeze/...)
        echo -e "${GREEN}✓ Squeeze Go tests passed.${NC}"
        echo ""
    else
        echo -e "${YELLOW}Notice: No Go backend test suite for '$TEST_PLUGIN'. Skipping Go tests.${NC}"
        echo ""
    fi

    echo -e "${BOLD}${CYAN}[Tests] Running .NET test suite...${NC}"

    declare -A TEST_PROJECTS
    TEST_PROJECTS["console"]="$DOTNET_DIR/MadTOM.Tests/Console/MadTOM.Console.Tests.csproj"
    TEST_PROJECTS["telemetry"]="$DOTNET_DIR/MadTOM.Tests/Plugins/Telemetry/MADTOM.Plugins.Telemetry.Tests.csproj"
    TEST_PROJECTS["squeeze"]="$DOTNET_DIR/MadTOM.Tests/Plugins/Squeeze/MADTOM.Plugins.Squeeze.Tests.csproj"
    TEST_PROJECTS["android"]="$DOTNET_DIR/MadTOM.Tests/Plugins/AndroidToolkit/MADTOM.Plugins.AndroidToolkit.Tests.csproj"
    TEST_PROJECTS["mediacenter"]="$DOTNET_DIR/MadTOM.Tests/Plugins/MediaCenter/MADTOM.Plugins.MediaCenter.Tests.csproj"
    TEST_PROJECTS["noxai"]="$DOTNET_DIR/MadTOM.Tests/Plugins/NoxAI/MADTOM.Plugins.NoxAI.Tests.csproj"
    TEST_PROJECTS["audiosync"]="$DOTNET_DIR/MadTOM.Tests/Plugins/AudioSync/MADTOM.Plugins.AudioSync.Tests.csproj"

    if [ -n "$TEST_PLUGIN" ]; then
        if [ -n "${TEST_PROJECTS[$TEST_PLUGIN]:-}" ]; then
            dotnet test "${TEST_PROJECTS[$TEST_PLUGIN]}" -c "$CONFIG" --nologo -v minimal
            echo -e "${GREEN}✓ Targeted .NET tests passed for $TEST_PLUGIN.${NC}"
        else
            echo -e "${RED}Unknown test plugin: $TEST_PLUGIN. Options: ${!TEST_PROJECTS[*]}${NC}"
            exit 1
        fi
    else
        for tp in "${TEST_PROJECTS[@]}"; do
            dotnet test "$tp" -c "$CONFIG" --nologo -v minimal
        done
        echo -e "${GREEN}✓ All decoupled .NET tests passed.${NC}"
    fi
    echo ""
fi

echo -e "${BOLD}${GREEN}=== All MADTOM components built successfully! ===${NC}"
