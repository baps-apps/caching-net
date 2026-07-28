using Caching.NET.Options;
using Caching.NET.Resilience;
using Caching.NET.Serialization;
using Caching.NET.Services;
using Caching.NET.Tests.Fakes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Tests.Telemetry;

/// <summary>
/// cache.operation.duration is recorded once per call at the routing layer. A backend service invoked
/// directly must record nothing, otherwise composite operations nest and every dashboard that sums
/// across operations double counts.
/// </summary>
public class OperationDurationTests
{
    private static RedisCacheService BuildRedis(IDistributedCache distributed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(distributed);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CacheOptions { KeyPrefix = "dur" }));
        services.AddSingleton<ICacheSerializer>(new JsonCacheSerializer());
        services.AddSingleton(CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromSeconds(5), retryCount: 0));
        services.AddSingleton<RedisCacheService>();
        return services.BuildServiceProvider().GetRequiredService<RedisCacheService>();
    }

    [Fact]
    public async Task Redis_service_called_directly_records_no_operation_duration()
    {
        var cache = BuildRedis(new FakeDistributedCache());
        var key = $"k:{Guid.NewGuid():N}";
        var (values, listener) = MeterListenerHelpers.Capture<double>("cache.operation.duration", "Redis");
        using var _ = listener;

        await cache.GetOrCreateAsync(key, _ => Task.FromResult("v"));
        await cache.GetAsync<string>(key);
        await cache.SetAsync(key, "v2");
        await cache.RemoveAsync(key);
        listener.Dispose();

        Assert.Empty(values);
    }
}
