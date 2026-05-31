using System.Diagnostics;

namespace FastFormat;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var paths = new List<string>();
        var includes = new List<string>();
        var excludes = new List<string>();
        bool check = false;
        bool verbose = false;
        bool force = false;
        bool cache = false;
        bool lsp = false;
        int? parallel = null;
        string? stdinFilePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--check":
                case "-c":
                case "--dry-run":
                    check = true;
                    break;
                case "--verbose":
                case "-v":
                    verbose = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--cache":
                    cache = true;
                    break;
                case "--lsp":
                    lsp = true;
                    break;
                case "--parallel":
                case "-p":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var p) || p < 1)
                        return Error("--parallel requires a positive integer value.");
                    parallel = p;
                    break;
                case "--include":
                    if (i + 1 >= args.Length)
                        return Error("--include requires a pattern argument.");
                    includes.Add(args[++i]);
                    break;
                case "--exclude":
                    if (i + 1 >= args.Length)
                        return Error("--exclude requires a pattern argument.");
                    excludes.Add(args[++i]);
                    break;
                case "--stdin-filepath":
                    if (i + 1 >= args.Length)
                        return Error("--stdin-filepath requires a path argument.");
                    stdinFilePath = args[++i];
                    break;
                case "--version":
                    PrintVersion();
                    return 0;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                default:
                    if (arg.StartsWith('-'))
                        return Error($"Unknown option: {arg}");
                    paths.Add(arg);
                    break;
            }
        }

        if (lsp)
        {
            using var lspCts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                lspCts.Cancel();
            };

            using var lspFormatter = new LspFormatterService();
            var server = new LspServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), lspFormatter);
            return await server.RunAsync(lspCts.Token);
        }

        // Detect stdin mode: explicit "-" or piped input with no paths
        bool stdinMode = paths.Remove("-") || (paths.Count == 0 && Console.IsInputRedirected);

        if (!stdinMode && !force)
        {
            foreach (var path in paths.Count == 0 ? ["."] : paths)
            {
                var fullPath = Path.GetFullPath(path);
                if (!Directory.Exists(fullPath))
                    continue;
                if (IsDangerousDirectory(fullPath))
                {
                    Console.Error.WriteLine(
                        $"Refusing to recursively format '{path}': this directory is too broad.\n" +
                        "Use --force if you really want to format it.");
                    return 1;
                }
                if (!LooksLikeProject(fullPath))
                {
                    Console.Error.WriteLine(
                        $"Refusing to recursively format '{path}': it does not look like a project directory.\n" +
                        "Use --force if you really want to format it.");
                    return 1;
                }
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var stopwatch = Stopwatch.StartNew();
        var formatter = new Formatter(check, verbose, parallel, includes, excludes, cache);

        int result;
        if (stdinMode)
        {
            result = await formatter.RunStdinAsync(stdinFilePath, cts.Token);
        }
        else
        {
            if (paths.Count == 0)
                paths.Add(".");
            result = await formatter.RunAsync(paths, cts.Token);
        }

        stopwatch.Stop();

        if (verbose && !stdinMode)
            Console.WriteLine($"Completed in {stopwatch.ElapsedMilliseconds}ms");

        return result;
    }
    static bool IsDangerousDirectory(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.GetPathRoot(path);
        return string.Equals(path, home, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
    }

    static bool LooksLikeProject(string path)
    {
        var dir = path;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return true;
            if (File.Exists(Path.Combine(dir, ".editorconfig")))
                return true;
            if (Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                return true;
            if (Directory.EnumerateFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Any()
                || Directory.EnumerateFiles(dir, "*.slnx", SearchOption.TopDirectoryOnly).Any())
                return true;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return false;
    }

    static int Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    static void PrintVersion()
    {
        var version = typeof(Program).Assembly.GetName().Version;
        Console.WriteLine($"FastFormat {version}");
    }

    static void PrintHelp()
    {
        Console.WriteLine("FastFormat - A fast C# formatter that respects .editorconfig");
        Console.WriteLine();
        Console.WriteLine("Usage: dotnet-fastformat [options] [paths...]");
        Console.WriteLine("       cat file.cs | dotnet-fastformat [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -c, --check, --dry-run Check formatting without making changes");
        Console.WriteLine("  -v, --verbose          Show detailed output");
        Console.WriteLine("  -p, --parallel N       Number of parallel workers (default: processor count)");
        Console.WriteLine("  --include PATTERN      Include files matching glob pattern (repeatable)");
        Console.WriteLine("  --exclude PATTERN      Exclude files matching glob pattern (repeatable)");
        Console.WriteLine("  --stdin-filepath PATH  File path to use for .editorconfig resolution in stdin mode");
        Console.WriteLine("  --cache                Enable content-hash cache (requires git repo)");
        Console.WriteLine("  --lsp                  Run as a stdio Language Server Protocol formatter");
        Console.WriteLine("  --force                Bypass project-directory safety check");
        Console.WriteLine("  --version              Show version");
        Console.WriteLine("  -h, --help             Show this help");
    }
}
