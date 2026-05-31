using FastFormat.Benchmark;

namespace FastFormat.Tests;

public sealed class BenchmarkCommandTests
{
    [Fact]
    public void FastFormatColdCommand_FormatsWorkspaceWithoutCheckOrCache()
    {
        var scenario = BenchmarkScenario.RequiredScenarios.Single(s => s.Kind == BenchmarkScenarioKind.ColdFormat);

        var command = BenchmarkCommand.CreateFastFormat("/tmp/fastformat", scenario, "/tmp/work");

        Assert.Equal("/tmp/fastformat", command.ExecutablePath);
        Assert.Equal(["/tmp/work"], command.Arguments);
        Assert.Equal([0], command.AllowedExitCodes);
    }

    [Fact]
    public void FastFormatDirtyCheckCommand_AllowsFormattingFailureExit()
    {
        var scenario = BenchmarkScenario.RequiredScenarios.Single(s => s.Kind == BenchmarkScenarioKind.DirtyCheck);

        var command = BenchmarkCommand.CreateFastFormat("/tmp/fastformat", scenario, "/tmp/work");

        Assert.Equal(["--check", "/tmp/work"], command.Arguments);
        Assert.Equal([1], command.AllowedExitCodes);
    }

    [Fact]
    public void FastFormatPartialCommand_UsesCache()
    {
        var scenario = BenchmarkScenario.RequiredScenarios.Single(s => s.Kind == BenchmarkScenarioKind.PartialCachedFormat);

        var command = BenchmarkCommand.CreateFastFormat("/tmp/fastformat", scenario, "/tmp/work");

        Assert.Equal(["--cache", "/tmp/work"], command.Arguments);
        Assert.Equal([0], command.AllowedExitCodes);
    }

    [Fact]
    public void DotnetFormatDirtyCheckCommand_UsesVerifyNoChanges()
    {
        var scenario = BenchmarkScenario.RequiredScenarios.Single(s => s.Kind == BenchmarkScenarioKind.DirtyCheck);

        var command = BenchmarkCommand.CreateDotnetFormat(scenario, "/tmp/work");

        Assert.Equal("dotnet", command.ExecutablePath);
        Assert.Equal(["format", "/tmp/work", "--verify-no-changes"], command.Arguments);
        Assert.Equal([2], command.AllowedExitCodes);
    }
}
