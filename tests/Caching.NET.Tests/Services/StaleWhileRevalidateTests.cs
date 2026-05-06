using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caching.NET.Tests.Services;

public class StaleWhileRevalidateTests
{
    private static ICacheService BuildService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("swr-test"));
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<ICacheService>();
    }

    [Fact]
    public async Task GetOrCreateAsync_returns_stale_value_within_window_and_schedules_refresh()
    {
        var svc = BuildService();
        var calls = 0;

        // Step 1: prime cache with absolute expiry of 100 ms, stale-for 1 s
        await svc.GetOrCreateAsync(
            "k",
            _ => { Interlocked.Increment(ref calls); return Task.FromResult("v1"); },
            callOptions: new CacheCallOptions
            {
                AbsoluteExpiration = TimeSpan.FromMilliseconds(100),
                AllowStaleFor = TimeSpan.FromSeconds(1),
            });
        Assert.Equal(1, calls);

        // Step 2: wait past abs expiry but inside stale window
        await Task.Delay(150);

        // Step 3: read should serve stale value AND schedule a background refresh
        var result = await svc.GetOrCreateAsync(
            "k",
            _ => { Interlocked.Increment(ref calls); return Task.FromResult("v2"); },
            callOptions: new CacheCallOptions
            {
                AbsoluteExpiration = TimeSpan.FromMilliseconds(100),
                AllowStaleFor = TimeSpan.FromSeconds(1),
            });

        Assert.Equal("v1", result); // stale value returned
        // refresh task scheduled; allow it to run
        await Task.Delay(150);
        Assert.True(calls >= 2, $"Expected background refresh to run; calls={calls}");
    }

    [Fact]
    public async Task GetOrCreateAsync_outside_stale_window_runs_factory_directly()
    {
        var svc = BuildService();
        var calls = 0;

        await svc.GetOrCreateAsync(
            "k",
            _ => { Interlocked.Increment(ref calls); return Task.FromResult("v1"); },
            callOptions: new CacheCallOptions
            {
                AbsoluteExpiration = TimeSpan.FromMilliseconds(50),
                AllowStaleFor = TimeSpan.FromMilliseconds(50),
            });

        await Task.Delay(200); // past abs + stale window

        var result = await svc.GetOrCreateAsync(
            "k",
            _ => { Interlocked.Increment(ref calls); return Task.FromResult("v2"); });

        Assert.Equal("v2", result);
        Assert.Equal(2, calls);
    }
}
