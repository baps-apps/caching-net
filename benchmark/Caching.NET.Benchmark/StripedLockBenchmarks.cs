using BenchmarkDotNet.Attributes;
using Caching.NET.Internal;

namespace Caching.NET.Benchmark;

[MemoryDiagnoser]
public class StripedLockBenchmarks
{
    private StripedLockManager _mgr = default!;

    [Params(1, 10, 100)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public void Setup() => _mgr = new StripedLockManager(1024);

    [GlobalCleanup]
    public void Cleanup() => _mgr.Dispose();

    [Benchmark]
    public async Task Acquire_Release_Same_Key()
    {
        var tasks = new Task[Concurrency];
        for (int i = 0; i < Concurrency; i++)
            tasks[i] = Task.Run(async () =>
            {
                var sem = _mgr.GetLock("hot");
                await sem.WaitAsync();
                try { }
                finally { sem.Release(); }
            });
        await Task.WhenAll(tasks);
    }
}
