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
            ["CacheOptions:DefaultExpiration"] = "00:10:00"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal(CacheMode.InMemory, options.Mode);
        Assert.Equal(TimeSpan.FromMinutes(10), options.GetDefaultExpiration());
    }

    [Fact]
    public void AddCaching_WithDisabled_RegistersNoOpService()
    {
        var config = new Dictionary<string, string?> { ["CacheOptions:Enabled"] = "false" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<Abstractions.ICacheService>();
        Assert.IsType<Caching.NET.Services.NoOpCacheService>(cache);
    }

    [Fact]
    public void AddCaching_WithDisabledAndInvalidValues_DoesNotThrow()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "false",
            ["CacheOptions:DefaultExpiration"] = "not-a-timespan"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();

        // Should not throw despite invalid DefaultExpiration because caching is disabled.
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.Equal("not-a-timespan", options.DefaultExpiration);
    }

    [Fact]
    public void AddCaching_WithEnabledAndInvalidValues_ThrowsValidationException()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "InMemory",
            ["CacheOptions:DefaultExpiration"] = "not-a-timespan"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();

        services.AddCaching(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
        {
            _ = provider.GetRequiredService<IOptions<CacheOptions>>().Value;
        });
    }

    [Fact]
    public void AddCaching_RedisModeWithoutConnectionString_Throws()
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = "true",
            ["CacheOptions:Mode"] = "Redis"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddCaching(configuration));
    }
}
