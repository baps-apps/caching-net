using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Caching.NET.Abstractions;
using Caching.NET.Extensions;

namespace Caching.NET.Tests.Validation;

public class CacheRegistrationValidationTests
{
    [Fact]
    public void ValidateCacheRegistration_WhenCachingEnabled_ResolvesICacheService()
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
        using var provider = services.BuildServiceProvider();

        var returned = provider.ValidateCacheRegistration();
        Assert.Same(provider, returned);

        var cache = provider.GetRequiredService<ICacheService>();
        Assert.NotNull(cache);
    }

    [Fact]
    public void ValidateCacheRegistration_WhenCachingDisabled_ResolvesNoOp()
    {
        var config = new Dictionary<string, string?> { ["CacheOptions:Enabled"] = "false" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddCaching(configuration);
        using var provider = services.BuildServiceProvider();

        provider.ValidateCacheRegistration();
        var cache = provider.GetRequiredService<ICacheService>();
        Assert.IsType<Caching.NET.Services.NoOpCacheService>(cache);
    }
}
