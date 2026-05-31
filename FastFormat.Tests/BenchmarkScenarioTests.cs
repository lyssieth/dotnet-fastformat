using FastFormat.Benchmark;

namespace FastFormat.Tests;

public sealed class BenchmarkScenarioTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"FastFormat.Benchmark.Tests.{Guid.NewGuid()}");

    public BenchmarkScenarioTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ColdSetup_UsesDirtyFilesWithoutGitOrCache()
    {
        var scenario = BenchmarkScenario.RequiredScenarios.Single(s => s.Kind == BenchmarkScenarioKind.ColdFormat);

        var workspace = await scenario.PrepareAsync(_tempDir, new BenchmarkSettings(FileCount: 6, PropertiesPerFile: 3), WarmFormattedAsync, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(workspace.RootDirectory, ".git")));
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, ".fastformat-cache")));
        Assert.Equal(6, workspace.TotalFileCount);
        Assert.Equal(6, workspace.CountDirtyFiles());
    }

    [Fact]
    public async Task PartialSetup_WarmsCacheThenDirtiesExactlyHalfTheFiles()
    {
        var scenario = BenchmarkScenario.RequiredScenarios.Single(s => s.Kind == BenchmarkScenarioKind.PartialCachedFormat);

        var workspace = await scenario.PrepareAsync(_tempDir, new BenchmarkSettings(FileCount: 7, PropertiesPerFile: 3), WarmFormattedAsync, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(workspace.RootDirectory, ".git")));
        Assert.True(File.Exists(Path.Combine(workspace.RootDirectory, ".fastformat-cache")));
        Assert.Equal(7, workspace.TotalFileCount);
        Assert.Equal(3, workspace.CountDirtyFiles());
    }

    private static Task WarmFormattedAsync(BenchmarkWorkspace workspace, CancellationToken cancellationToken)
    {
        workspace.MarkAllFormatted();
        File.WriteAllText(Path.Combine(workspace.RootDirectory, ".fastformat-cache"), "warm-cache\n");
        return Task.CompletedTask;
    }
}
