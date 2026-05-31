namespace FastFormat.Benchmark;

internal enum BenchmarkScenarioKind
{
    ColdFormat,
    DirtyCheck,
    CleanCheck,
    PartialCachedFormat,
}

internal sealed record BenchmarkSettings(int FileCount = 100, int PropertiesPerFile = 200);

internal sealed record BenchmarkScenario(
    BenchmarkScenarioKind Kind,
    string Name,
    bool IsCheck,
    bool UseCache,
    bool IncludeGit,
    bool NeedsWarmFormat,
    bool DirtyHalfAfterWarm)
{
    public static readonly BenchmarkScenario[] RequiredScenarios =
    [
        new(BenchmarkScenarioKind.ColdFormat, "Cold", IsCheck: false, UseCache: false, IncludeGit: false, NeedsWarmFormat: false, DirtyHalfAfterWarm: false),
        new(BenchmarkScenarioKind.DirtyCheck, "Dirty check", IsCheck: true, UseCache: false, IncludeGit: false, NeedsWarmFormat: false, DirtyHalfAfterWarm: false),
        new(BenchmarkScenarioKind.CleanCheck, "Clean check", IsCheck: true, UseCache: false, IncludeGit: false, NeedsWarmFormat: true, DirtyHalfAfterWarm: false),
        new(BenchmarkScenarioKind.PartialCachedFormat, "Partial", IsCheck: false, UseCache: true, IncludeGit: true, NeedsWarmFormat: true, DirtyHalfAfterWarm: true),
    ];

    public async Task<BenchmarkWorkspace> PrepareAsync(
        string rootDirectory,
        BenchmarkSettings settings,
        Func<BenchmarkWorkspace, CancellationToken, Task> warmFormatAsync,
        CancellationToken cancellationToken)
    {
        var workspace = BenchmarkWorkspace.Create(rootDirectory, settings, IncludeGit);

        if (NeedsWarmFormat)
        {
            await warmFormatAsync(workspace, cancellationToken);
        }

        if (DirtyHalfAfterWarm)
        {
            workspace.DirtyFirstFiles(settings.FileCount / 2);
        }

        return workspace;
    }
}

internal sealed class BenchmarkWorkspace
{
    private readonly int _propertiesPerFile;

    private BenchmarkWorkspace(string rootDirectory, int totalFileCount, int propertiesPerFile)
    {
        RootDirectory = rootDirectory;
        TotalFileCount = totalFileCount;
        _propertiesPerFile = propertiesPerFile;
    }

    public string RootDirectory { get; }
    public int TotalFileCount { get; }

    public static BenchmarkWorkspace Create(string rootDirectory, BenchmarkSettings settings, bool includeGit)
    {
        if (Directory.Exists(rootDirectory))
            Directory.Delete(rootDirectory, recursive: true);
        Directory.CreateDirectory(rootDirectory);

        File.WriteAllText(Path.Combine(rootDirectory, ".editorconfig"), "root = true\n[*.cs]\nindent_style = space\nindent_size = 4\n");
        File.WriteAllText(Path.Combine(rootDirectory, "Benchmark.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");

        if (includeGit)
            Directory.CreateDirectory(Path.Combine(rootDirectory, ".git"));

        var workspace = new BenchmarkWorkspace(rootDirectory, settings.FileCount, settings.PropertiesPerFile);
        for (var i = 1; i <= settings.FileCount; i++)
        {
            workspace.WriteFile(i, formatted: false);
        }

        return workspace;
    }

    public int CountDirtyFiles()
    {
        var dirty = 0;
        for (var i = 1; i <= TotalFileCount; i++)
        {
            var content = File.ReadAllText(GetFilePath(i));
            if (content.Contains("{get;set;}", StringComparison.Ordinal))
                dirty++;
        }

        return dirty;
    }

    public void MarkAllFormatted()
    {
        for (var i = 1; i <= TotalFileCount; i++)
        {
            WriteFile(i, formatted: true);
        }
    }

    public void DirtyFirstFiles(int count)
    {
        for (var i = 1; i <= count && i <= TotalFileCount; i++)
        {
            WriteFile(i, formatted: false);
        }
    }

    public string GetFilePath(int index) => Path.Combine(RootDirectory, $"File{index:0000}.cs");

    private void WriteFile(int index, bool formatted)
    {
        using var writer = new StreamWriter(GetFilePath(index), false);
        writer.WriteLine("namespace BenchmarkGenerated;");
        writer.WriteLine($"public class File{index:0000}");
        writer.WriteLine("{");
        for (var property = 1; property <= _propertiesPerFile; property++)
        {
            writer.Write("    public int Prop");
            writer.Write(property);
            writer.WriteLine(formatted ? " { get; set; }" : " {get;set;}");
        }
        writer.WriteLine("}");
    }
}
