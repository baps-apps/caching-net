using BenchmarkDotNet.Attributes;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Benchmark;

[MemoryDiagnoser]
public class GetOrCreateBenchmarks
{
    private ICacheService _inMemory = default!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
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
        const string key = "miss";
        await _inMemory.RemoveAsync(key);
        return await _inMemory.GetOrCreateAsync(key, _ => Task.FromResult("v"));
    }
}
