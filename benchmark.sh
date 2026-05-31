#!/usr/bin/env bash
set -euo pipefail

# Reproducible benchmark for FastFormat
# Usage: ./benchmark.sh [--verbose] [file_count] [lines_per_file]

VERBOSE=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --verbose|-v)
            VERBOSE=true
            shift
            ;;
        *)
            break
            ;;
    esac
done

FILE_COUNT=${1:-100}
LINES_PER_FILE=${2:-200}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET_FORMAT="dotnet format"

TEMP_DIR=$(mktemp -d)
trap 'rm -rf "$TEMP_DIR"' EXIT

generate_file() {
    local n=$1
    local path="$TEMP_DIR/File$(printf '%04d' $n).cs"
    {
        echo "namespace BenchmarkGenerated;"
        echo "public class File$(printf '%04d' $n)"
        echo "{"
        for i in $(seq 1 $LINES_PER_FILE); do
            echo "    public int Prop$i {get;set;}"
        done
        echo "}"
    } > "$path"
}

setup_dir() {
    rm -rf "$TEMP_DIR"/*
    echo "root = true" > "$TEMP_DIR/.editorconfig"
    cat > "$TEMP_DIR/Benchmark.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF
}

run_fastformat() {
    "$FASTFORMAT_BIN" "$TEMP_DIR" >/dev/null 2>&1 || true
}

run_fastformat_check() {
    "$FASTFORMAT_BIN" --check "$TEMP_DIR" >/dev/null 2>&1 || true
}

run_dotnet_format() {
    $DOTNET_FORMAT "$TEMP_DIR" >/dev/null 2>&1 || true
}

run_dotnet_format_check() {
    $DOTNET_FORMAT "$TEMP_DIR" --verify-no-changes >/dev/null 2>&1 || true
}

benchmark_cmd() {
    local label="$1"
    local setup_fn="$2"
    local run_fn="$3"
    local best=999999
    for run in $(seq 1 5); do
        $setup_fn
        local start end elapsed
        start=$(date +%s%N)
        if $VERBOSE; then
            $run_fn
        else
            $run_fn >/dev/null 2>&1
        fi
        end=$(date +%s%N)
        elapsed=$(( (end - start) / 1000000 ))
        if [[ $elapsed -lt $best ]]; then
            best=$elapsed
        fi
        if $VERBOSE; then
            echo "  Run $run: ${elapsed}ms"
        fi
    done
    echo "$label: ${best}ms (best of 5)"
}

echo "=== FastFormat Benchmark ==="
echo "Files: $FILE_COUNT"
echo "Lines per file: $LINES_PER_FILE"
echo ""

cd "$SCRIPT_DIR"
echo "Building FastFormat (Release)..."
dotnet build -c Release --verbosity quiet > /dev/null
FASTFORMAT_BIN="$SCRIPT_DIR/bin/Release/net10.0/FastFormat"

# Check dotnet format availability
HAS_DOTNET_FORMAT=false
if dotnet format --version >/dev/null 2>&1; then
    HAS_DOTNET_FORMAT=true
fi

# Cold formatting: unformatted files, actual rewrite
echo "--- Cold formatting (unformatted files) ---"

setup_cold() {
    setup_dir
    for i in $(seq 1 $FILE_COUNT); do generate_file $i; done
}

benchmark_cmd "FastFormat" setup_cold run_fastformat

if $HAS_DOTNET_FORMAT; then
    benchmark_cmd "dotnet format (cold)" setup_cold run_dotnet_format
fi

echo ""

# No-op formatting: already formatted files
echo "--- No-op formatting (already formatted) ---"

setup_dir
for i in $(seq 1 $FILE_COUNT); do generate_file $i; done
"$FASTFORMAT_BIN" "$TEMP_DIR" >/dev/null 2>&1 || true

setup_noop() {
    # Files already formatted from initial pass
    :
}

benchmark_cmd "FastFormat" setup_noop run_fastformat

if $HAS_DOTNET_FORMAT; then
    setup_dir
    for i in $(seq 1 $FILE_COUNT); do generate_file $i; done
    $DOTNET_FORMAT "$TEMP_DIR" >/dev/null 2>&1 || true
    benchmark_cmd "dotnet format (hot)" setup_noop run_dotnet_format
fi

echo ""

# Check mode: detect changes without writing
echo "--- Check mode ---"

benchmark_cmd "FastFormat --check" setup_cold run_fastformat_check

if $HAS_DOTNET_FORMAT; then
    benchmark_cmd "dotnet format --verify-no-changes" setup_cold run_dotnet_format_check
fi

echo ""
echo "=== Benchmark complete ==="
