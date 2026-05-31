#!/usr/bin/env bash
set -euo pipefail

# Reproducible benchmark for FastFormat
# Usage: ./benchmark.sh [file_count] [lines_per_file]

FILE_COUNT=${1:-100}
LINES_PER_FILE=${2:-200}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMP_DIR=$(mktemp -d)
trap 'rm -rf "$TEMP_DIR"' EXIT

echo "=== FastFormat Benchmark ==="
echo "Files: $FILE_COUNT"
echo "Lines per file: $LINES_PER_FILE"
echo "Temp dir: $TEMP_DIR"
echo ""

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

echo "Generating $FILE_COUNT C# files..."
for i in $(seq 1 $FILE_COUNT); do
    generate_file $i
done

# Dummy project files so both formatters can run
echo "root = true" > "$TEMP_DIR/.editorconfig"
cat > "$TEMP_DIR/Benchmark.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF

echo "Done."
echo ""

# Build FastFormat in release mode first
echo "Building FastFormat (Release)..."
cd "$SCRIPT_DIR"
dotnet build -c Release --verbosity quiet > /dev/null

echo ""
echo "=== Running FastFormat ==="
time dotnet run -c Release --no-build -- "$TEMP_DIR"

echo ""

# Optionally benchmark dotnet format if available
if dotnet format --version > /dev/null 2>&1; then
    echo "=== Running dotnet format ==="
    # Restore the files first
    for i in $(seq 1 $FILE_COUNT); do generate_file $i; done
    echo "root = true" > "$TEMP_DIR/.editorconfig"
    time dotnet format "$TEMP_DIR" --verbosity diagnostic
else
    echo "dotnet format not available; skipping comparison."
fi

echo ""
echo "=== Benchmark complete ==="
