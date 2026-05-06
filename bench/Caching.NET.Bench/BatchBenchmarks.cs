using BenchmarkDotNet.Attributes;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caching.NET.Bench;

[MemoryDiagnoser]
public class BatchBenchmarks
{
    private ICacheService _cache = default!;
    private string[] _keys = Array.Empty<string>();

    [Params(10, 100)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("bench-batch"));
        _cache = services.BuildServiceProvider().GetRequiredService<ICacheService>();
        _keys = Enumerable.Range(0, N).Select(i => $"k{i}").ToArray();
        var dict = _keys.ToDictionary(k => k, _ => "v");
        _cache.SetManyAsync(dict).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task GetMany() => await _cache.GetManyAsync<string>(_keys);
}
