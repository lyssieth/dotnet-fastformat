namespace FastFormat.Benchmark;

internal sealed record BenchmarkCommand(string ExecutablePath, string[] Arguments, int[] AllowedExitCodes)
{
    public static BenchmarkCommand CreateFastFormat(string executablePath, BenchmarkScenario scenario, string workspacePath)
    {
        var arguments = new List<string>(3);
        if (scenario.IsCheck)
            arguments.Add("--check");
        if (scenario.UseCache)
            arguments.Add("--cache");
        arguments.Add(workspacePath);

        return new BenchmarkCommand(executablePath, arguments.ToArray(), ExpectedExitCodes(scenario));
    }

    public static BenchmarkCommand CreateDotnetFormat(BenchmarkScenario scenario, string workspacePath)
    {
        var arguments = new List<string>(3) { "format", workspacePath };
        if (scenario.IsCheck)
            arguments.Add("--verify-no-changes");

        return new BenchmarkCommand("dotnet", arguments.ToArray(), DotnetFormatExitCodes(scenario));
    }

    private static int[] ExpectedExitCodes(BenchmarkScenario scenario)
    {
        return scenario.Kind switch
        {
            BenchmarkScenarioKind.DirtyCheck => [1],
            _ => [0],
        };
    }

    private static int[] DotnetFormatExitCodes(BenchmarkScenario scenario)
    {
        return scenario.Kind switch
        {
            BenchmarkScenarioKind.DirtyCheck => [2],
            _ => [0],
        };
    }
}
