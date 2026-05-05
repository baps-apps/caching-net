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
            ["CacheOptions:KeyPrefix"] = "svc:v1",
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
        Assert.Equal("svc:v1", options.KeyPrefix);
    }

    [Fact]
    public void AddCaching_WithDisabled_StillRegistersICacheService()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:KeyPrefix"] = "svc:v1"
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
            ["CacheOptions:KeyPrefix"] = "svc:v1"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddCaching(configuration));
    }
}
