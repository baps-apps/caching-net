using Caching.NET.Options;
using Caching.NET.Services;
using Caching.NET.Tests.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options;
using Xunit;

namespace Caching.NET.Tests.Services;

public class InMemoryEvictionTests
{
    [Fact]
    public async Task Removing_entry_emits_eviction_with_reason_Removed()
    {
        var modeTag = "InMemory";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.evictions", modeTag);
        using var l1 = listener;

        using var memory = new MemoryCache(new MemoryCacheOptions());
        var opts = MsOptions.Options.Create(new CacheOptions { KeyPrefix = "evict-test" });
        var service = new InMemoryCacheService(memory, opts, NullLogger<InMemoryCacheService>.Instance);

        await service.SetAsync("k", "v");
        await service.RemoveAsync("k");

        // PostEvictionCallback runs on a queued work item; pump the thread pool briefly.
        await Task.Delay(50);

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.eviction_reason" && (string?)t.Value == "Removed"));
    }

    [Fact]
    public async Task Expiring_entry_emits_eviction_with_reason_Expired()
    {
        var modeTag = "InMemory";
        var (values, listener) = MeterListenerHelpers.Capture<long>("cache.evictions", modeTag);
        using var l2 = listener;

        using var memory = new MemoryCache(new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromMilliseconds(10) });
        var opts = MsOptions.Options.Create(new CacheOptions { KeyPrefix = "evict-test" });
        var service = new InMemoryCacheService(memory, opts, NullLogger<InMemoryCacheService>.Instance);

        await service.SetAsync("k", "v", expiration: TimeSpan.FromMilliseconds(20));
        await Task.Delay(120);
        // Trigger expiration scan with another op
        memory.TryGetValue("k", out _);
        await Task.Delay(50);

        Assert.Contains(values, v => v.tags.Any(t => t.Key == "cache.eviction_reason" && (string?)t.Value == "Expired"));
    }
}
