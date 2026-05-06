using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Caching.NET.Tests.Builder;

public class CachingBuilderP2Tests
{
    [Fact]
    public void WithTtlJitter_writes_value_to_options()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("p2").WithTtlJitter(0.20));
        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(0.20, opts.TtlJitterPercentage);
    }

    [Fact]
    public void WithStaleRefreshConcurrency_writes_value_to_options()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("p2").WithStaleRefreshConcurrency(64));
        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(64, opts.StaleRefreshConcurrency);
    }

    [Fact]
    public void RequireTagSupport_with_Hybrid_succeeds()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.UseHybrid().WithKeyPrefix("p2").RequireTagSupport());
        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.True(opts.RequireTagSupport);
        Assert.Equal(CacheMode.Hybrid, opts.Mode);
    }

    [Fact]
    public void RequireTagSupport_with_InMemory_throws_at_resolution()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("p2").RequireTagSupport());
        using var sp = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() =>
            sp.GetRequiredService<IOptions<CacheOptions>>().Value);
        Assert.Contains("RequireTagSupport", ex.Message);
    }
}
