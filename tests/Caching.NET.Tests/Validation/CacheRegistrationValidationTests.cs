using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Microsoft.Extensions.Options;

namespace Caching.NET.Tests.Validation;

public class CacheRegistrationValidationTests
{
    [Fact]
    public void ValidateCacheRegistration_WhenCachingEnabled_ResolvesICacheService()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        var returned = provider.ValidateCacheRegistration();
        Assert.Same(provider, returned);

        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);
    }

    [Fact]
    public void ValidateCacheRegistration_WhenCachingDisabled_StillResolvesICacheService()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:KeyPrefix"] = "test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        provider.ValidateCacheRegistration();
        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);
    }

    [Fact]
    public void ValidateCacheRegistration_WhenKeyPrefixMissing_ThrowsValidationException()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);

        var ex = Assert.Throws<OptionsValidationException>(() => services.BuildServiceProvider().ValidateCacheRegistration());
        Assert.Contains("KeyPrefix", ex.Message);
    }

    [Fact]
    public void ValidateCacheRegistration_WhenHybridWithoutRedisConnection_ThrowsInvalidOperationException()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "Hybrid",
            ["CacheOptions:KeyPrefix"] = "test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddCaching(configuration));
        Assert.Contains("RedisConnectionString", ex.Message);
    }
}
