using Caching.NET.Extensions;
using Caching.NET.Keys;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caching.NET.Tests.Keys;

public sealed class CacheKeyFactoryTests
{
    [Fact]
    public void AddCaching_registers_DefaultCacheKeyFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(c => c.UseInMemory().WithKeyPrefix("test"));

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<ICacheKeyFactory>();

        Assert.IsType<DefaultCacheKeyFactory>(factory);
        Assert.Equal(
            CacheKey.For<string>("x").Build(),
            factory.For<string>("x").Build());
    }

    [Fact]
    public void Custom_ICacheKeyFactory_registered_before_AddCaching_is_not_replaced()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICacheKeyFactory, StubTenantCacheKeyFactory>();
        services.AddCaching(c => c.UseInMemory().WithKeyPrefix("test"));

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<ICacheKeyFactory>();

        Assert.IsType<StubTenantCacheKeyFactory>(factory);
        Assert.Equal("Product:a:v1:tenant-a", factory.For<Product>("a").Build());
    }

    private sealed class Product;

    private sealed class StubTenantCacheKeyFactory : ICacheKeyFactory
    {
        public CacheKeyBuilder For<T>(object id) =>
            CacheKey.For<T>(id).WithSegment("v1").WithSegment("tenant-a");
    }
}
