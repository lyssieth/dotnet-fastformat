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
FASTFORMAT="dotnet run -c Release --no-build --"
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

generate_formatted_file() {
    local n=$1
    local path="$TEMP_DIR/File$(printf '%04d' $n).cs"
    {
        echo "namespace BenchmarkGenerated;"
        echo "public class File$(printf '%04d' $n)"
        echo "{"
        for i in $(seq 1 $LINES_PER_FILE); do
            echo "    public int Prop$i { get; set; }"
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

benchmark_cmd() {
    local label="$1"
    shift
    local best=999999
    local output=""
    for run in $(seq 1 5); do
        local start end elapsed
        start=$(date +%s%N)
        if $VERBOSE; then
            "$@"
        else
            "$@" >/dev/null 2>&1
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
dotnet build -c Release --verbosity quiet > /dev/null

# Cold formatting: unformatted files, actual rewrite
echo "--- Cold formatting (unformatted files) ---"
setup_dir
for i in $(seq 1 $FILE_COUNT); do generate_file $i; done
benchmark_cmd "FastFormat" bash -c "$FASTFORMAT >/dev/null 2>&1" "$TEMP_DIR"

if $VERBOSE; then
    setup_dir
    for i in $(seq 1 $FILE_COUNT); do generate_file $i; done
    echo "  dotnet format (cold, with restore):"
    benchmark_cmd "  dotnet format" $DOTNET_FORMAT "$TEMP_DIR"

    setup_dir
    for i in $(seq 1 $FILE_COUNT); do generate_file $i; done
    # warm up restore first
    $DOTNET_FORMAT "$TEMP_DIR" >/dev/null 2>&1 || true
    for i in $(seq 1 $FILE_COUNT); do generate_file $i; done
    echo "  dotnet format (hot, post-restore):"
    benchmark_cmd "  dotnet format" $DOTNET_FORMAT "$TEMP_DIR"
fi

echo ""

# No-op formatting: already formatted files
echo "--- No-op formatting (already formatted) ---"
setup_dir
for i in $(seq 1 $FILE_COUNT); do generate_file $i; done
# Format once so files are already clean
$FASTFORMAT "$TEMP_DIR" >/dev/null 2>&1 || true
benchmark_cmd "FastFormat" bash -c "$FASTFORMAT >/dev/null 2>&1" "$TEMP_DIR"

echo ""

# Check mode: detect changes without writing
echo "--- Check mode ---"
setup_dir
for i in $(seq 1 $FILE_COUNT); do generate_file $i; done
benchmark_cmd "FastFormat --check" bash -c "$FASTFORMAT --check >/dev/null 2>&1" "$TEMP_DIR"

echo ""
echo "=== Benchmark complete ==="
