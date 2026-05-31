using System.Collections.Concurrent;
using System.Text;
using GlobExpressions;
using System.Text.RegularExpressions;
using System.IO.Hashing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;

namespace FastFormat;

internal class FormatterResult
{
    public int FilesProcessed;
    public int FilesChanged;
    public int FilesWithErrors;
}

internal class Formatter
{
    private static readonly ConcurrentBag<AdhocWorkspace> _workspacePool = new();

    private readonly bool _check;
    private readonly bool _verbose;
    private readonly int _parallel;
    private readonly List<string> _includes;
    private readonly List<string> _excludes;
    private readonly bool _useCache;

    public Formatter(bool check, bool verbose, int? parallel, List<string>? includes = null, List<string>? excludes = null, bool useCache = false)
    {
        _check = check;
        _verbose = verbose;
        _parallel = parallel ?? Environment.ProcessorCount;
        _includes = includes ?? new List<string>();
        _excludes = excludes ?? new List<string>();
        _useCache = useCache;
    }

    public async Task<int> RunStdinAsync(string? filePath, CancellationToken cancellationToken = default)
    {
        var text = await Console.In.ReadToEndAsync(cancellationToken);
        try
        {
            var formatted = await FormatTextAsync(text, filePath);
            await Console.Out.WriteAsync(formatted);
            return 0;
        }
        catch (InvalidOperationException)
        {
            await Console.Out.WriteAsync(text);
            return 2;
        }
    }

    public async Task<int> RunAsync(List<string> paths, CancellationToken cancellationToken = default)
    {
        var files = FindFiles(paths);
        files = await GitIgnoreFilter.FilterIgnoredFilesAsync(files, cancellationToken);
        var result = new FormatterResult();

        if (!files.Any())
        {
            Console.WriteLine("No C# files found.");
            return 0;
        }

        if (_verbose)
            Console.WriteLine($"Found {files.Count} C# file(s)");

        FormatCache? cache = null;
        string? gitRoot = null;

        if (_useCache || files.Count > 0)
        {
            var searchDir = Path.GetDirectoryName(files[0]) ?? Directory.GetCurrentDirectory();
            gitRoot = GitIgnoreFilter.FindGitRoot(searchDir);

            if (_useCache && gitRoot == null)
            {
                Console.Error.WriteLine("Warning: --cache requires a git repository. Cache disabled.");
            }
            else if (gitRoot != null)
            {
                var cacheExists = File.Exists(Path.Combine(gitRoot, ".fastformat-cache"));
                if (_useCache || cacheExists)
                    cache = new FormatCache(gitRoot);
            }
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = _parallel, CancellationToken = cancellationToken };

        await Parallel.ForEachAsync(files, options, async (file, ct) =>
        {
            try
            {
                var changed = await FormatFileAsync(file, cache, gitRoot, ct);
                Interlocked.Increment(ref result.FilesProcessed);
                if (changed) Interlocked.Increment(ref result.FilesChanged);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref result.FilesProcessed);
                Interlocked.Increment(ref result.FilesWithErrors);
                if (_verbose)
                    Console.WriteLine($"Error formatting {file}: {ex.Message}");
            }
        });

        cache?.Flush();

        var action = _check ? "checked" : "formatted";
        Console.WriteLine($"{action} {result.FilesProcessed} file(s), {result.FilesChanged} changed.");

        if (result.FilesWithErrors > 0)
            return 2;

        if (_check && result.FilesChanged > 0)
        {
            Console.WriteLine($"Formatting issues found in {result.FilesChanged} file(s).");
            return 1;
        }

        return 0;
    }

    private async Task<bool> FormatFileAsync(string filePath, FormatCache? cache, string? gitRoot, CancellationToken ct)
    {
        var (text, encoding, hasBom, bytes) = await ReadFileWithEncodingAsync(filePath, ct);

        string? relativePath = null;
        if (cache != null && gitRoot != null)
        {
            relativePath = GetRelativePathForCache(filePath, gitRoot);
            var hash = ComputeHash(bytes);
            if (cache.TryGet(relativePath, hash))
            {
                if (_verbose)
                    Console.WriteLine($"Cache hit: {filePath}");
                return false;
            }
        }

        var formatted = await FormatTextAsync(text, filePath);
        if (text == formatted)
        {
            if (cache != null && relativePath != null)
            {
                var hash = ComputeHash(bytes);
                cache.Set(relativePath, hash);
            }
            return false;
        }

        if (_check)
        {
            if (_verbose)
                Console.WriteLine($"Would format: {filePath}");
            return true;
        }

        var preamble = hasBom ? encoding.GetPreamble() : Array.Empty<byte>();
        var textBytes = encoding.GetBytes(formatted);
        var outputBytes = new byte[preamble.Length + textBytes.Length];
        preamble.CopyTo(outputBytes, 0);
        textBytes.CopyTo(outputBytes, preamble.Length);

        await File.WriteAllBytesAsync(filePath, outputBytes, ct);

        if (_verbose)
            Console.WriteLine($"Formatted: {filePath}");

        if (cache != null && relativePath != null)
        {
            var hash = ComputeHash(outputBytes);
            cache.Set(relativePath, hash);
        }

        return true;
    }

    private static byte[] ComputeHash(byte[] data)
    {
        return XxHash128.Hash(data);
    }

    private static string GetRelativePathForCache(string filePath, string gitRoot)
    {
        var rel = Path.GetRelativePath(gitRoot, filePath);
        return rel.Replace('\\', '/');
    }

    private static async Task<(string Text, Encoding Encoding, bool HasBom, byte[] Bytes)> ReadFileWithEncodingAsync(string filePath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var encoding = DetectEncoding(bytes, out var bomLength);
        var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        return (text, encoding, bomLength > 0, bytes);
    }

    private static Encoding DetectEncoding(byte[] bytes, out int bomLength)
    {
        bomLength = 0;
        // Check 4-byte BOMs before 2-byte BOMs to avoid misidentifying UTF-32 as UTF-16.
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            bomLength = 4;
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            bomLength = 4;
            return Encoding.UTF32;
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bomLength = 3;
            return Encoding.UTF8;
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            bomLength = 2;
            return Encoding.BigEndianUnicode;
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            bomLength = 2;
            return Encoding.Unicode;
        }
        return Encoding.UTF8;
    }

    private static AdhocWorkspace RentWorkspace()
    {
        if (_workspacePool.TryTake(out var ws))
            return ws;
        return new AdhocWorkspace();
    }

    private static void ReturnWorkspace(AdhocWorkspace workspace)
    {
        var solution = workspace.CurrentSolution;
        foreach (var projectId in solution.ProjectIds.ToArray())
            solution = solution.RemoveProject(projectId);
        workspace.TryApplyChanges(solution);
        _workspacePool.Add(workspace);
    }

    private async Task<string> FormatTextAsync(string text, string? filePath)
    {
        var sourceText = SourceText.From(text);

        // Parse the syntax tree
        var tree = CSharpSyntaxTree.ParseText(sourceText, path: filePath ?? "stdin.cs");
        var root = await tree.GetRootAsync();

        // Check for parse errors
        var diagnostics = tree.GetDiagnostics();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            if (_verbose && filePath != null)
                Console.WriteLine($"Skipping {filePath} - parse errors detected");
            throw new InvalidOperationException("Parse errors detected.");
        }

        // Build formatting options from .editorconfig
        var workspace = RentWorkspace();
        try
        {
            var options = workspace.Options;
            EditorConfigOptions? editorConfig = null;

            if (filePath != null)
            {
                editorConfig = EditorConfigParser.GetOptionsForFile(filePath);
                options = ApplyEditorConfigOptions(options, editorConfig);
            }

            // Organize imports if configured
            if (editorConfig?.SortSystemDirectivesFirst == true || editorConfig?.SeparateImportDirectiveGroups == true)
            {
                var project = workspace.AddProject("temp", LanguageNames.CSharp);
                var documentInfo = DocumentInfo.Create(
                    DocumentId.CreateNewId(project.Id),
                    Path.GetFileName(filePath ?? "stdin.cs"),
                    filePath: filePath,
                    loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Default)));
                var document = workspace.AddDocument(documentInfo);
                document = await Microsoft.CodeAnalysis.Formatting.Formatter.OrganizeImportsAsync(document);
                var organizedRoot = await document.GetSyntaxRootAsync();
                if (organizedRoot != null)
                {
                    root = organizedRoot;
                    sourceText = await document.GetTextAsync();
                }
            }

            var formattedRoot = Microsoft.CodeAnalysis.Formatting.Formatter.Format(root, workspace, options);

            var formattedText = formattedRoot.GetText().ToString();

            // Handle insert_final_newline manually since Roslyn formatter doesn't always
            var endsWithNewline = !string.IsNullOrEmpty(formattedText) &&
                (formattedText[^1] == '\n' || formattedText[^1] == '\r');
            var shouldInsertFinalNewline = editorConfig?.InsertFinalNewline != false;
            if (shouldInsertFinalNewline && !endsWithNewline)
            {
                var newline = editorConfig?.NewLine ?? "\n";
                formattedText += newline;
            }
            else if (!shouldInsertFinalNewline && endsWithNewline)
            {
                formattedText = formattedText.TrimEnd('\n', '\r');
            }

            // Handle trim_trailing_whitespace manually
            if (editorConfig?.TrimTrailingWhitespace == true)
            {
                formattedText = System.Text.RegularExpressions.Regex.Replace(formattedText, @"[ \t]+(\r?\n|\r)", "$1");
                formattedText = formattedText.TrimEnd(' ', '\t');
            }
            // Normalize line endings if configured
            if (!string.IsNullOrEmpty(editorConfig?.NewLine))
            {
                formattedText = formattedText.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", editorConfig.NewLine);
            }
            return formattedText;
        }
        finally
        {
            ReturnWorkspace(workspace);
        }
    }

    private static OptionSet ApplyEditorConfigOptions(OptionSet options, EditorConfigOptions editorConfig)
    {
        if (editorConfig.IndentSize.HasValue)
            options = options.WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, editorConfig.IndentSize.Value);

        if (editorConfig.TabWidth.HasValue)
            options = options.WithChangedOption(FormattingOptions.TabSize, LanguageNames.CSharp, editorConfig.TabWidth.Value);

        if (editorConfig.UseTabs.HasValue)
            options = options.WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, editorConfig.UseTabs.Value);

        if (editorConfig.NewLine != null)
            options = options.WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, editorConfig.NewLine);

        if (editorConfig.NewLineBeforeOpenBrace != null)
        {
            var value = editorConfig.NewLineBeforeOpenBrace.ToLowerInvariant();
            var allBraces = new[]
            {
                CSharpFormattingOptions.NewLinesForBracesInTypes,
                CSharpFormattingOptions.NewLinesForBracesInMethods,
                CSharpFormattingOptions.NewLinesForBracesInProperties,
                CSharpFormattingOptions.NewLinesForBracesInAccessors,
                CSharpFormattingOptions.NewLinesForBracesInAnonymousMethods,
                CSharpFormattingOptions.NewLinesForBracesInControlBlocks,
                CSharpFormattingOptions.NewLinesForBracesInAnonymousTypes,
                CSharpFormattingOptions.NewLinesForBracesInObjectCollectionArrayInitializers,
                CSharpFormattingOptions.NewLinesForBracesInLambdaExpressionBody,
            };

            if (value == "none" || value == "")
            {
                foreach (var brace in allBraces)
                    options = options.WithChangedOption(brace, false);
            }
            else if (value == "all")
            {
                foreach (var brace in allBraces)
                    options = options.WithChangedOption(brace, true);
            }
            else
            {
                var parts = value.Split(',').Select(p => p.Trim()).ToHashSet();
                foreach (var brace in allBraces)
                    options = options.WithChangedOption(brace, false);

                foreach (var part in parts)
                {
                    options = part switch
                    {
                        "types" => options.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInTypes, true),
                        "methods" => options.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInMethods, true),
                        "properties" => options.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInProperties, true),
                        "accessors" => options.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInAccessors, true),
                        "anonymous_methods" => options.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInAnonymousMethods, true),
                        "control_blocks" => options.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInControlBlocks, true),
                        "anonymous_types" => options.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInAnonymousTypes, true),
                        "object_collection_array_initializers" => options.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInObjectCollectionArrayInitializers, true),
                        "lambda_expression_body" => options.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInLambdaExpressionBody, true),
                        _ => options
                    };
                }
            }
        }

        if (editorConfig.NewLineBeforeCatch.HasValue)
            options = options.WithChangedOption(CSharpFormattingOptions.NewLineForCatch, editorConfig.NewLineBeforeCatch.Value);

        if (editorConfig.NewLineBeforeElse.HasValue)
            options = options.WithChangedOption(CSharpFormattingOptions.NewLineForElse, editorConfig.NewLineBeforeElse.Value);

        if (editorConfig.NewLineBeforeFinally.HasValue)
            options = options.WithChangedOption(CSharpFormattingOptions.NewLineForFinally, editorConfig.NewLineBeforeFinally.Value);

        if (editorConfig.NewLineBeforeMembersInObjectInitializers.HasValue)
            options = options.WithChangedOption(CSharpFormattingOptions.NewLineForMembersInObjectInit, editorConfig.NewLineBeforeMembersInObjectInitializers.Value);

        if (editorConfig.NewLineBetweenQueryExpressionClauses.HasValue)
            options = options.WithChangedOption(CSharpFormattingOptions.NewLineForClausesInQuery, editorConfig.NewLineBetweenQueryExpressionClauses.Value);

        return options;
    }

    private List<string> FindFiles(List<string> paths)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);

            if (File.Exists(fullPath))
            {
                if (IsCSharpFile(fullPath))
                    files.Add(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                foreach (var pattern in new[] { "*.cs", "*.csx" })
                {
                    foreach (var file in Directory.EnumerateFiles(fullPath, pattern, SearchOption.AllDirectories))
                    {
                        // Skip common non-source directories
                        var dir = Path.GetDirectoryName(file) ?? "";
                        var parts = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                            continue;

                        files.Add(file);
                    }
                }
            }
        }

        var result = files.ToList();

        // Apply include patterns
        if (_includes.Count > 0)
        {
            result = result.Where(f => _includes.Any(p => MatchesGlobPattern(f, p))).ToList();
        }

        // Apply exclude patterns
        if (_excludes.Count > 0)
        {
            result = result.Where(f => !_excludes.Any(p => MatchesGlobPattern(f, p))).ToList();
        }

        return result;
    }

    private static bool IsCSharpFile(string path)
    {
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);
    }
    private static bool MatchesGlobPattern(string filePath, string pattern)
    {
        var g = new Glob(pattern, GlobOptions.Compiled);
        var fileName = Path.GetFileName(filePath);
        return g.IsMatch(filePath) || g.IsMatch(fileName);
    }
}
