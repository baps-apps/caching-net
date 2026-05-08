using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Caching.NET.Extensions;
using Caching.NET.Options;

namespace Caching.NET.Tests.Options;

public class CacheOptionsValidationTests
{
    [Fact]
    public void AddCaching_WithValidInMemoryConfig_BindsAndValidates()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "orders-api",
            ["CacheOptions:DefaultExpiration"] = "00:10:00"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal(CacheMode.InMemory, options.Mode);
        Assert.Equal(TimeSpan.FromMinutes(10), options.DefaultExpiration);
        Assert.Equal("orders-api", options.KeyPrefix);
    }

    [Fact]
    public void AddCaching_WithDisabled_StillRegistersICacheService()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:KeyPrefix"] = "orders-api"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<Abstractions.ICacheService>();
        Assert.NotNull(cache);
    }

    [Fact]
    public void AddCaching_RedisModeWithoutConnectionString_Throws()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "Redis",
            ["CacheOptions:KeyPrefix"] = "orders-api"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddCaching(configuration));
    }

    [Fact]
    public void AddCaching_WithInvalidPayloadCompressionThreshold_Throws()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:KeyPrefix"] = "orders-api",
            ["CacheOptions:PayloadCompressionThresholdBytes"] = "128"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<CacheOptions>>().Value);
    }
}
