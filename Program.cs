using System.Diagnostics;

namespace FastFormat;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var paths = new List<string>();
        bool check = false;
        bool verbose = false;
        int? parallel = null;
        string? stdinFilePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--check":
                case "-c":
                    check = true;
                    break;
                case "--verbose":
                case "-v":
                    verbose = true;
                    break;
                case "--parallel":
                case "-p":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var p))
                        parallel = p;
                    break;
                case "--stdin-filepath":
                    if (i + 1 < args.Length)
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
                    if (!arg.StartsWith('-'))
                        paths.Add(arg);
                    break;
            }
        }

        // Detect stdin mode: explicit "-" or piped input with no paths
        bool stdinMode = paths.Remove("-") || (paths.Count == 0 && Console.IsInputRedirected);

        var stopwatch = Stopwatch.StartNew();
        var formatter = new Formatter(check, verbose, parallel);

        int result;
        if (stdinMode)
        {
            result = await formatter.RunStdinAsync(stdinFilePath);
        }
        else
        {
            if (paths.Count == 0)
                paths.Add(".");
            result = await formatter.RunAsync(paths);
        }

        stopwatch.Stop();

        if (verbose && !stdinMode)
            Console.WriteLine($"Completed in {stopwatch.ElapsedMilliseconds}ms");

        return result;
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
        Console.WriteLine("  -c, --check            Check formatting without making changes");
        Console.WriteLine("  -v, --verbose          Show detailed output");
        Console.WriteLine("  -p, --parallel N       Number of parallel workers (default: processor count)");
        Console.WriteLine("  --stdin-filepath PATH  File path to use for .editorconfig resolution in stdin mode");
        Console.WriteLine("  --version              Show version");
        Console.WriteLine("  -h, --help             Show this help");
    }
}
