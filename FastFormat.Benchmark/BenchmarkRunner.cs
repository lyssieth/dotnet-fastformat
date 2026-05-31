using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FastFormat.Benchmark;

internal static class BenchmarkRunner
{
    private const string TargetFramework = "net10.0";

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseOptions(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 1;
        }

        var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
        var projectPath = Path.Combine(repoRoot, "FastFormat.csproj");
        var settings = new BenchmarkSettings(options.FileCount, options.PropertiesPerFile);

        Console.WriteLine("=== FastFormat Benchmark ===");
        Console.WriteLine($"Files: {settings.FileCount}");
        Console.WriteLine($"Properties per file: {settings.PropertiesPerFile}");
        Console.WriteLine($"Runs: {options.Runs}");
        Console.WriteLine();

        Console.WriteLine("Building FastFormat (Release)...");
        var build = await RunProcessAsync(new BenchmarkCommand("dotnet", ["build", projectPath, "-c", "Release", "--verbosity", "quiet"], [0]), repoRoot, options.Verbose, cancellationToken);
        if (!build.Succeeded)
        {
            Console.Error.WriteLine("FastFormat Release build failed.");
            return 1;
        }

        var fastFormatPath = Path.Combine(repoRoot, "bin", "Release", TargetFramework, ExecutableName("FastFormat"));
        if (!File.Exists(fastFormatPath))
        {
            Console.Error.WriteLine($"FastFormat executable not found: {fastFormatPath}");
            return 1;
        }

        var nativeAotPath = await TryPublishNativeAotAsync(projectPath, repoRoot, options.Verbose, cancellationToken);
        var hasDotnetFormat = (await RunProcessAsync(new BenchmarkCommand("dotnet", ["format", "--version"], [0]), repoRoot, false, cancellationToken)).Succeeded;

        if (nativeAotPath == null)
            Console.WriteLine("NativeAOT: unavailable for this run");
        else
            Console.WriteLine($"NativeAOT: {nativeAotPath}");
        Console.WriteLine(hasDotnetFormat ? "dotnet format: available" : "dotnet format: unavailable");
        Console.WriteLine();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"FastFormat.Benchmark.{Guid.NewGuid()}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            PrintHeader(nativeAotPath != null, hasDotnetFormat);
            foreach (var scenario in BenchmarkScenario.RequiredScenarios)
            {
                var fast = await MeasureAsync(
                    scenario,
                    settings,
                    tempRoot,
                    options.Runs,
                    workspace => BenchmarkCommand.CreateFastFormat(fastFormatPath, scenario, workspace.RootDirectory),
                    workspace => WarmWithFastFormatAsync(fastFormatPath, workspace, scenario.UseCache, cancellationToken),
                    options.Verbose,
                    cancellationToken);

                long? native = null;
                if (nativeAotPath != null)
                {
                    native = await MeasureAsync(
                        scenario,
                        settings,
                        tempRoot,
                        options.Runs,
                        workspace => BenchmarkCommand.CreateFastFormat(nativeAotPath, scenario, workspace.RootDirectory),
                        workspace => WarmWithFastFormatAsync(nativeAotPath, workspace, scenario.UseCache, cancellationToken),
                        options.Verbose,
                        cancellationToken);
                }

                long? dotnet = null;
                if (hasDotnetFormat)
                {
                    dotnet = await MeasureAsync(
                        scenario,
                        settings,
                        tempRoot,
                        options.Runs,
                        workspace => BenchmarkCommand.CreateDotnetFormat(scenario, workspace.RootDirectory),
                        workspace => WarmWithDotnetFormatAsync(workspace, cancellationToken),
                        options.Verbose,
                        cancellationToken);
                }

                PrintRow(scenario.Name, fast, native, dotnet, nativeAotPath != null, hasDotnetFormat);
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("=== Benchmark complete ===");
        return 0;
    }

    private static async Task<long> MeasureAsync(
        BenchmarkScenario scenario,
        BenchmarkSettings settings,
        string tempRoot,
        int runs,
        Func<BenchmarkWorkspace, BenchmarkCommand> commandFactory,
        Func<BenchmarkWorkspace, Task> warmFormatAsync,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var best = long.MaxValue;
        for (var run = 1; run <= runs; run++)
        {
            var workspacePath = Path.Combine(tempRoot, $"{Sanitize(scenario.Name)}-{Guid.NewGuid():N}");
            var workspace = await scenario.PrepareAsync(workspacePath, settings, (w, _) => warmFormatAsync(w), cancellationToken);
            var command = commandFactory(workspace);

            var stopwatch = Stopwatch.StartNew();
            var result = await RunProcessAsync(command, workspace.RootDirectory, verbose, cancellationToken);
            stopwatch.Stop();

            if (!result.Succeeded)
                throw new InvalidOperationException($"Benchmark command failed for {scenario.Name}: {command.ExecutablePath} {string.Join(' ', command.Arguments)} exited {result.ExitCode}");

            if (stopwatch.ElapsedMilliseconds < best)
                best = stopwatch.ElapsedMilliseconds;

            if (verbose)
                Console.WriteLine($"  {scenario.Name} run {run}: {stopwatch.ElapsedMilliseconds}ms");

            try { Directory.Delete(workspace.RootDirectory, recursive: true); } catch { }
        }

        return best;
    }

    private static Task WarmWithFastFormatAsync(string fastFormatPath, BenchmarkWorkspace workspace, bool useCache, CancellationToken cancellationToken)
    {
        var scenario = useCache
            ? BenchmarkScenario.RequiredScenarios.Single(s => s.Kind == BenchmarkScenarioKind.PartialCachedFormat)
            : BenchmarkScenario.RequiredScenarios.Single(s => s.Kind == BenchmarkScenarioKind.ColdFormat);
        return RunRequiredAsync(BenchmarkCommand.CreateFastFormat(fastFormatPath, scenario, workspace.RootDirectory), workspace.RootDirectory, cancellationToken);
    }

    private static Task WarmWithDotnetFormatAsync(BenchmarkWorkspace workspace, CancellationToken cancellationToken)
    {
        return RunRequiredAsync(BenchmarkCommand.CreateDotnetFormat(BenchmarkScenario.RequiredScenarios[0], workspace.RootDirectory), workspace.RootDirectory, cancellationToken);
    }

    private static async Task RunRequiredAsync(BenchmarkCommand command, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(command, workingDirectory, false, cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Warmup command failed: {command.ExecutablePath} {string.Join(' ', command.Arguments)} exited {result.ExitCode}");
    }

    private static async Task<string?> TryPublishNativeAotAsync(string projectPath, string repoRoot, bool verbose, CancellationToken cancellationToken)
    {
        var rid = CurrentRid();
        if (rid == null)
            return null;

        Console.WriteLine($"Publishing FastFormat NativeAOT ({rid})...");
        var publish = await RunProcessAsync(
            new BenchmarkCommand("dotnet", ["publish", projectPath, "-c", "Release", "-r", rid, "-p:PublishAot=true", "--verbosity", "minimal"], [0]),
            repoRoot,
            verbose: true,
            cancellationToken);

        if (!publish.Succeeded)
            return null;

        var path = Path.Combine(repoRoot, "bin", "Release", TargetFramework, rid, "publish", ExecutableName("FastFormat"));
        if (!File.Exists(path))
            return null;

        var smokeRoot = Path.Combine(Path.GetTempPath(), $"FastFormat.Benchmark.AotSmoke.{Guid.NewGuid():N}");
        try
        {
            var workspace = BenchmarkWorkspace.Create(smokeRoot, new BenchmarkSettings(FileCount: 1, PropertiesPerFile: 1), includeGit: false);
            var smoke = await RunProcessAsync(BenchmarkCommand.CreateFastFormat(path, BenchmarkScenario.RequiredScenarios[0], workspace.RootDirectory), workspace.RootDirectory, verbose, cancellationToken);
            if (smoke.Succeeded)
                return path;

            Console.WriteLine($"NativeAOT smoke test failed with exit code {smoke.ExitCode}; excluding NativeAOT from benchmark output.");
            return null;
        }
        finally
        {
            try { Directory.Delete(smokeRoot, recursive: true); } catch { }
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(BenchmarkCommand command, string workingDirectory, bool verbose, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdout;
        var error = await stderr;

        if (verbose)
        {
            if (!string.IsNullOrWhiteSpace(output))
                Console.Write(output);
            if (!string.IsNullOrWhiteSpace(error))
                Console.Error.Write(error);
        }

        return new ProcessResult(process.ExitCode, command.AllowedExitCodes.Contains(process.ExitCode));
    }

    private static bool TryParseOptions(string[] args, out BenchmarkOptions options, out string error)
    {
        var verbose = false;
        Span<int> values = stackalloc int[3];
        var valueCount = 0;

        foreach (var arg in args)
        {
            if (arg is "--verbose" or "-v")
            {
                verbose = true;
                continue;
            }

            if (valueCount == values.Length || !int.TryParse(arg, out var value) || value < 1)
            {
                options = default;
                error = $"Invalid benchmark argument: {arg}";
                return false;
            }

            values[valueCount++] = value;
        }

        options = new BenchmarkOptions(
            verbose,
            valueCount > 0 ? values[0] : 100,
            valueCount > 1 ? values[1] : 200,
            valueCount > 2 ? values[2] : 5);
        error = string.Empty;
        return true;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: ./benchmark.sh [--verbose] [file_count] [properties_per_file] [runs]");
    }

    private static string FindRepoRoot(string startDirectory)
    {
        var directory = startDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "FastFormat.csproj")))
                return directory;
            var parent = Path.GetDirectoryName(directory);
            if (parent == directory)
                break;
            directory = parent!;
        }

        throw new InvalidOperationException("Could not find FastFormat.csproj from current directory.");
    }

    private static string? CurrentRid()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : null;
        if (os == null)
            return null;

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };

        return arch == null ? null : $"{os}-{arch}";
    }

    private static string ExecutableName(string name)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;
    }

    private static string Sanitize(string value)
    {
        return value.Replace(' ', '-').ToLowerInvariant();
    }

    private static void PrintHeader(bool includeNative, bool includeDotnet)
    {
        Console.Write("| Scenario | FastFormat |");
        if (includeNative)
            Console.Write(" NativeAOT | AOT ratio |");
        if (includeDotnet)
            Console.Write(" dotnet format | dotnet ratio |");
        Console.WriteLine();

        Console.Write("| --- | ---: |");
        if (includeNative)
            Console.Write(" ---: | ---: |");
        if (includeDotnet)
            Console.Write(" ---: | ---: |");
        Console.WriteLine();
    }

    private static void PrintRow(string scenario, long fast, long? native, long? dotnet, bool includeNative, bool includeDotnet)
    {
        Console.Write($"| {scenario} | {fast}ms |");
        if (includeNative)
            Console.Write($" {FormatMs(native)} | {FormatRatio(fast, native)} |");
        if (includeDotnet)
            Console.Write($" {FormatMs(dotnet)} | {FormatRatio(fast, dotnet)} |");
        Console.WriteLine();
    }

    private static string FormatMs(long? value) => value.HasValue ? $"{value.Value}ms" : "n/a";

    private static string FormatRatio(long fast, long? other)
    {
        if (!other.HasValue || fast <= 0)
            return "n/a";
        var ratio = other.Value / (double)fast;
        return $"{ratio:0.0}x";
    }

    private readonly record struct BenchmarkOptions(bool Verbose, int FileCount, int PropertiesPerFile, int Runs);
    private readonly record struct ProcessResult(int ExitCode, bool Succeeded);
}
