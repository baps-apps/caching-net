using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.NET.Tests.Services;

public class RoutingCacheServiceConcurrencyTests
{
    [Fact]
    public async Task CoalesceConcurrent_ReducesFactoryInvocations_PerKey()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "test"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var callOptions = new CacheCallOptions { CoalesceConcurrent = true };
        var counter = 0;

        async Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref counter);
            await Task.Delay(20, _).ConfigureAwait(false);
            return "value";
        }

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => cache.GetOrCreateAsync("coalesce:key", Factory, callOptions, cancellationToken: CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks.Select(t => t));

        Assert.Equal(1, counter);
    }

    [Fact]
    public async Task WhenDisabled_CoalesceConcurrent_SkipsCoalescing()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var callOptions = new CacheCallOptions { CoalesceConcurrent = true };
        var counter = 0;

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => cache.GetOrCreateAsync(
                "disabled:coalesce",
                ct =>
                {
                    Interlocked.Increment(ref counter);
                    return Task.FromResult("value");
                },
                callOptions,
                cancellationToken: CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks.Select(t => t));

        Assert.Equal(5, counter);
    }
}

