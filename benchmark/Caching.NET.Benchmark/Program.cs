using BenchmarkDotNet.Running;

namespace Caching.NET.Benchmark;

/// <summary>
/// Benchmark entry point.
/// </summary>
/// <remarks>
/// <c>dotnet run -c Release -- --filter *</c> runs the Redis-free suites.
/// Set <c>CACHINGNET_BENCH_REDIS</c> to include the Redis and Hybrid suites.
/// </remarks>
public static class Program
{
    /// <summary>Runs the benchmark suites.</summary>
    /// <param name="args">BenchmarkDotNet arguments.</param>
    public static void Main(string[] args)
    {
        var suites = new List<Type>
        {
            typeof(InMemoryBenchmarks),
            typeof(SerializationBenchmarks),
            typeof(TelemetryOverheadBenchmarks),
            typeof(LayerDecoratorBenchmarks)
        };

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RedisBenchmarks.ConnectionStringVariable)))
        {
            suites.Add(typeof(RedisBenchmarks));
        }
        else
        {
            Console.WriteLine(
                $"Skipping Redis and Hybrid benchmarks: {RedisBenchmarks.ConnectionStringVariable} is not set.");
        }

        BenchmarkSwitcher.FromTypes([.. suites]).Run(args);
    }
}
