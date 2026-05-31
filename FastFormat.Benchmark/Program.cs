namespace FastFormat.Benchmark;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await BenchmarkRunner.RunAsync(args, CancellationToken.None);
    }
}
