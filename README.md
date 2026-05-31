# FastFormat

A fast C# formatter that respects `.editorconfig` rules. Designed to be significantly faster than `dotnet format` for pure formatting tasks.

## Performance

In benchmarks formatting 50 C# files:

- **FastFormat**: ~0.7s
- **`dotnet format`**: ~4.0s

That's roughly **6x faster**.
Run `benchmark.sh` yourself to reproduce:
```bash
./benchmark.sh 100 200
```

## Installation

```bash
dotnet tool install --global FastFormat
```

## Usage

```bash
# Format files or directories
# (refuses to run on non-project directories without --force)
dotnet-fastformat src/
dotnet-fastformat Program.cs

# Check formatting without making changes
dotnet-fastformat --check src/

# Include/exclude patterns
dotnet-fastformat --exclude "**/*.generated.cs" src/
dotnet-fastformat --include "src/**/*.cs" --include "tests/**/*.cs" .

# Bypass the project-directory safety check
dotnet-fastformat --force ~/some-random-dir/

# Stdin -> stdout
cat Program.cs | dotnet-fastformat
cat Program.cs | dotnet-fastformat --stdin-filepath src/Program.cs

# Verbose output
dotnet-fastformat -v src/

# Control parallelism
dotnet-fastformat -p 8 src/
```

## Supported .editorconfig Options

- `indent_style`
- `indent_size`
- `tab_width`
- `end_of_line`
- `insert_final_newline`
- `trim_trailing_whitespace`
- `csharp_new_line_before_open_brace`
- `csharp_new_line_before_catch`
- `csharp_new_line_before_else`
- `csharp_new_line_before_finally`
- `csharp_new_line_before_members_in_object_initializers`
- `csharp_new_line_between_query_expression_clauses`
- `dotnet_sort_system_directives_first`
- `dotnet_separate_import_directive_groups`

## File Types

- `.cs` — C# source files
- `.csx` — C# script files

## Safety

When run on a directory, FastFormat refuses to recurse unless the directory (or an ancestor) looks like a project — indicated by the presence of `.git`, `.editorconfig`, `*.csproj`, `*.sln`, or `*.slnx`. It also refuses to run directly on `$HOME` or the filesystem root. Use `--force` to bypass this check.

## Encoding

Files are read and written preserving their existing BOM. Files without a BOM are assumed to be UTF-8.

## Default Behaviors

- **Final newline**: Files are always given a trailing newline unless `.editorconfig` explicitly sets `insert_final_newline = false`.

## Why is it faster?

- No MSBuild workspace loading
- No semantic analysis
- Parallel file processing
- Direct `.editorconfig` parsing without heavy workspace machinery
- Gitignore-aware file discovery
