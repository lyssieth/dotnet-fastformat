using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;

namespace FastFormat;

class FormatterResult
{
    public int FilesProcessed { get; set; }
    public int FilesChanged { get; set; }
    public int FilesWithErrors { get; set; }
}

class Formatter
{
    private readonly bool _check;
    private readonly bool _verbose;
    private readonly int _parallel;

    public Formatter(bool check, bool verbose, int? parallel)
    {
        _check = check;
        _verbose = verbose;
        _parallel = parallel ?? Environment.ProcessorCount;
    }

    public async Task<int> RunStdinAsync(string? filePath)
    {
        var text = await Console.In.ReadToEndAsync();
        var formatted = await FormatTextAsync(text, filePath);
        if (formatted == null)
            return 2;
        await Console.Out.WriteAsync(formatted);
        return 0;
    }

    public async Task<int> RunAsync(List<string> paths)
    {
        var files = FindFiles(paths);
        files = GitIgnoreFilter.FilterIgnoredFiles(files);
        var result = new FormatterResult();

        if (!files.Any())
        {
            Console.WriteLine("No .cs files found.");
            return 0;
        }

        if (_verbose)
            Console.WriteLine($"Found {files.Count} .cs file(s)");

        var lockObj = new object();
        var options = new ParallelOptions { MaxDegreeOfParallelism = _parallel };

        await Parallel.ForEachAsync(files, options, async (file, ct) =>
        {
            try
            {
                var changed = await FormatFileAsync(file, ct);
                lock (lockObj)
                {
                    result.FilesProcessed++;
                    if (changed) result.FilesChanged++;
                }
            }
            catch (Exception ex)
            {
                lock (lockObj)
                {
                    result.FilesProcessed++;
                    result.FilesWithErrors++;
                }
                if (_verbose)
                    Console.WriteLine($"Error formatting {file}: {ex.Message}");
            }
        });

        if (_check && result.FilesChanged > 0)
        {
            Console.WriteLine($"Formatting issues found in {result.FilesChanged} file(s).");
            return 1;
        }

        var action = _check ? "checked" : "formatted";
        Console.WriteLine($"{action} {result.FilesProcessed} file(s), {result.FilesChanged} changed.");

        if (result.FilesWithErrors > 0)
            return 2;

        return 0;
    }

    private async Task<bool> FormatFileAsync(string filePath, CancellationToken ct)
    {
        var (text, encoding, hasBom) = await ReadFileWithEncodingAsync(filePath, ct);
        var formatted = await FormatTextAsync(text, filePath);
        if (formatted == null)
            return false;

        if (text == formatted)
            return false;

        if (_check)
        {
            if (_verbose)
                Console.WriteLine($"Would format: {filePath}");
            return true;
        }

        await WriteFileWithEncodingAsync(filePath, formatted, encoding, hasBom, ct);

        if (_verbose)
            Console.WriteLine($"Formatted: {filePath}");

        return true;
    }

    private static async Task<(string Text, Encoding Encoding, bool HasBom)> ReadFileWithEncodingAsync(string filePath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var encoding = DetectEncoding(bytes, out var bomLength);
        var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        return (text, encoding, bomLength > 0);
    }

    private static async Task WriteFileWithEncodingAsync(string filePath, string text, Encoding encoding, bool hasBom, CancellationToken ct)
    {
        var preamble = hasBom ? encoding.GetPreamble() : Array.Empty<byte>();
        var textBytes = encoding.GetBytes(text);
        var result = new byte[preamble.Length + textBytes.Length];
        preamble.CopyTo(result, 0);
        textBytes.CopyTo(result, preamble.Length);
        await File.WriteAllBytesAsync(filePath, result, ct);
    }

    private static Encoding DetectEncoding(byte[] bytes, out int bomLength)
    {
        bomLength = 0;
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
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            bomLength = 4;
            return Encoding.UTF32;
        }
        return Encoding.UTF8;
    }

    private async Task<string?> FormatTextAsync(string text, string? filePath)
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
            // For stdin, we still return the original text on error
            if (filePath == null)
                return text;
            return null;
        }

        // Build formatting options from .editorconfig
        var workspace = new AdhocWorkspace();
        var options = workspace.Options;

        if (filePath != null)
        {
            var editorConfig = EditorConfigParser.GetOptionsForFile(filePath);
            options = ApplyEditorConfigOptions(options, editorConfig);
        }

        var formattedRoot = Microsoft.CodeAnalysis.Formatting.Formatter.Format(root, workspace, options);

        var formattedText = formattedRoot.GetText().ToString();

        // Handle insert_final_newline manually since Roslyn formatter doesn't always
        if (filePath != null)
        {
            var editorConfig = EditorConfigParser.GetOptionsForFile(filePath);
            if (editorConfig.InsertFinalNewline == true && !formattedText.EndsWith('\n'))
                formattedText += "\n";
            else if (editorConfig.InsertFinalNewline == false && formattedText.EndsWith('\n'))
                formattedText = formattedText.TrimEnd('\n', '\r');
        }

        return formattedText;
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

        return options;
    }

    private static List<string> FindFiles(List<string> paths)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);

            if (File.Exists(fullPath))
            {
                if (fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    files.Add(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                foreach (var file in Directory.EnumerateFiles(fullPath, "*.cs", SearchOption.AllDirectories))
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

        return files.ToList();
    }
}
