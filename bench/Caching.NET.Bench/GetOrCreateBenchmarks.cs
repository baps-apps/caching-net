using BenchmarkDotNet.Attributes;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caching.NET.Bench;

[MemoryDiagnoser]
public class GetOrCreateBenchmarks
{
    private ICacheService _inMemory = default!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("bench"));
        _inMemory = services.BuildServiceProvider().GetRequiredService<ICacheService>();
        _inMemory.SetAsync("hot", "v").GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<string> Hit_Hot_Key()
        => await _inMemory.GetOrCreateAsync("hot", _ => Task.FromResult("v"));

    [Benchmark]
    public async Task<string> Miss_With_Factory()
    {
        var key = $"k-{Random.Shared.Next()}";
        return await _inMemory.GetOrCreateAsync(key, _ => Task.FromResult("v"));
    }
}
