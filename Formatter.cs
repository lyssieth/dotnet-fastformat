using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
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
        var text = await File.ReadAllTextAsync(filePath, ct);
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

        await File.WriteAllTextAsync(filePath, formatted, ct);

        if (_verbose)
            Console.WriteLine($"Formatted: {filePath}");

        return true;
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

        // Use AdhocWorkspace to get editorconfig options for this file path
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("temp", LanguageNames.CSharp);
        var documentInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id),
            Path.GetFileName(filePath ?? "stdin.cs"),
            filePath: filePath,
            loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Default)));
        var document = workspace.AddDocument(documentInfo);

        var options = await document.GetOptionsAsync();
        var formattedRoot = Microsoft.CodeAnalysis.Formatting.Formatter.Format(root, workspace, options);

        var formattedText = formattedRoot.GetText();
        return formattedText.ToString();
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
                                       p.Equals("node_modules", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    files.Add(file);
                }
            }
        }

        return files.ToList();
    }
}
