# FastFormat

A fast C# formatter that supports common formatting-related `.editorconfig` options. Designed for rapid development with format-on-save workflows.

FastFormat is **not a replacement for `dotnet format`**. It is a fast intermediary between *no formatting* and *waiting several seconds for a full `dotnet format` run*. Use it when you want formatting feedback quickly, then rely on `dotnet format` (or CI) for comprehensive validation.

## Performance

`benchmark.sh` creates a synthetic C# project, builds FastFormat in Release mode, attempts a NativeAOT publish, then compares against `dotnet format`.

The benchmark covers four runs:

| Scenario | Setup |
| --- | --- |
| Cold | No `.git`, no cache, dirty files, format |
| Dirty check | Dirty files, `--check` |
| Clean check | Formatted files, `--check` |
| Partial | Dirty files plus `.git`, warm `--cache`, dirty 50% of files, re-format with cache |

If the NativeAOT publish succeeds and the published binary passes a smoke formatting run, the output includes a NativeAOT column. Current Roslyn Workspaces dependencies publish with AOT warnings but fail the smoke run, so NativeAOT is not enabled for packaging yet.

Caveats: this is a synthetic corpus, not a substitute for benchmarking your real solution. `dotnet format` does MSBuild workspace loading and broader analysis; FastFormat intentionally skips that work to be a fast format-on-save/intermediate formatter.

Run it yourself:

```bash
./benchmark.sh 100 200
./benchmark.sh --verbose 200 500 5
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

# Long-running stdio LSP mode for editor format-on-save
dotnet-fastformat --lsp

# Verbose output
dotnet-fastformat -v src/

# Control parallelism
dotnet-fastformat -p 8 src/
```

## LSP Mode

`dotnet-fastformat --lsp` starts a stdio Language Server Protocol server. It advertises full-document sync, `textDocument/formatting`, and `textDocument/rangeFormatting`, so editors can keep one FastFormat process alive and avoid paying Roslyn startup on every save.

LSP mode never writes files directly. It returns `TextEdit[]` responses to the editor and uses `file://` document URIs for `.editorconfig` resolution. Full-document formatting integrates with the existing `.git/fastformat-cache`: if the current document hash is known to be formatted, FastFormat returns no edits without invoking Roslyn. The server also keeps a bounded in-memory cache for repeated formatting requests during the same session.

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
