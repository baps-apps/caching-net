using Caching.NET;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Registration;

public class RegistrationTests
{
    [Fact]
    public void DefaultRegistration_ResolvesTheCacheOperationContract()
    {
        using var provider = TestHost.BuildInMemory();

        var cache = provider.GetRequiredService<IFusionCache>();

        Assert.NotNull(cache);
        Assert.Equal(CachingDefaults.DefaultCacheName, cache.CacheName);
    }

    [Fact]
    public void DefaultRegistration_DoesNotRequireApplicationsToRegisterTheEngine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests"));

        // Nothing in the container should require the application to have registered engine
        // infrastructure itself: AddCaching is the only cache registration call.
        using var provider = services.BuildServiceProvider(validateScopes: true);
        Assert.NotNull(provider.GetRequiredService<IFusionCache>());
        Assert.NotNull(provider.GetRequiredService<ICacheProvider>());
        Assert.NotNull(provider.GetRequiredService<ICacheGuard>());
    }

    [Fact]
    public void CacheServices_AreSingletons()
    {
        using var provider = TestHost.BuildInMemory();

        Assert.Same(provider.GetRequiredService<IFusionCache>(), provider.GetRequiredService<IFusionCache>());
        Assert.Same(provider.GetRequiredService<ICacheProvider>(), provider.GetRequiredService<ICacheProvider>());
        Assert.Same(provider.GetRequiredService<ICacheGuard>(), provider.GetRequiredService<ICacheGuard>());
    }

    [Fact]
    public void CacheResolvedFromAScope_IsTheSameSingletonInstance()
    {
        using var provider = TestHost.BuildInMemory();
        var root = provider.GetRequiredService<IFusionCache>();

        using var scope = provider.CreateScope();
        var scoped = scope.ServiceProvider.GetRequiredService<IFusionCache>();

        Assert.Same(root, scoped);
    }

    [Fact]
    public void NamedCaches_AreResolvableByKeyAndThroughTheProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests"));
        services.AddCaching("short-lived", cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithDefaultExpiration(TimeSpan.FromSeconds(30)));

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var caches = provider.GetRequiredService<ICacheProvider>();

        Assert.Equal(["default", "short-lived"], caches.CacheNames.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Same(caches.GetCache("short-lived"), provider.GetRequiredKeyedService<IFusionCache>("short-lived"));
        Assert.Same(caches.Default, provider.GetRequiredService<IFusionCache>());
        Assert.NotSame(caches.Default, caches.GetCache("short-lived"));
    }

    [Fact]
    public async Task NamedCaches_DoNotShareEntries()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests"));
        services.AddCaching("other", cache => cache.UseInMemory().WithApplicationPrefix("tests"));

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var caches = provider.GetRequiredService<ICacheProvider>();

        await caches.Default.SetAsync("shared-key", 1);

        var isolated = await caches.GetCache("other").TryGetAsync<int>("shared-key");
        Assert.False(isolated.HasValue);
    }

    [Fact]
    public void DuplicateCacheName_FailsWithAnActionableMessage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching("dupe", cache => cache.UseInMemory().WithApplicationPrefix("tests"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddCaching("dupe", cache => cache.UseInMemory().WithApplicationPrefix("tests")));

        Assert.Contains("already registered", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dupe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateDefaultRegistration_Fails()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests"));

        Assert.Throws<InvalidOperationException>(
            () => services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests")));
    }

    [Fact]
    public void UnknownCacheName_FailsWithAListOfRegisteredNames()
    {
        using var provider = TestHost.BuildInMemory();
        var caches = provider.GetRequiredService<ICacheProvider>();

        var ex = Assert.Throws<InvalidOperationException>(() => caches.GetCache("nope"));

        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("default", ex.Message, StringComparison.Ordinal);
        Assert.Null(caches.GetCacheOrNull("nope"));
    }

    [Fact]
    public void SeparateServiceCollections_DoNotShareRegistrationState()
    {
        var first = new ServiceCollection();
        first.AddLogging();
        first.AddCaching("app", cache => cache.UseInMemory().WithApplicationPrefix("tests"));

        var second = new ServiceCollection();
        second.AddLogging();

        // No static registry: the same cache name in an independent container must be allowed.
        second.AddCaching("app", cache => cache.UseInMemory().WithApplicationPrefix("tests"));

        using var firstProvider = first.BuildServiceProvider();
        using var secondProvider = second.BuildServiceProvider();
        Assert.NotSame(
            firstProvider.GetRequiredService<ICacheProvider>().GetCache("app"),
            secondProvider.GetRequiredService<ICacheProvider>().GetCache("app"));
    }

    [Fact]
    public async Task DisabledCache_RunsTheFactoryEveryTimeAndCachesNothing()
    {
        using var provider = TestHost.Build(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .Disable());

        var cache = provider.Cache();
        var calls = 0;

        for (var i = 0; i < 3; i++)
        {
            var value = await cache.GetOrSetAsync<int>("key", async _ =>
            {
                Interlocked.Increment(ref calls);
                return 42;
            });
            Assert.Equal(42, value);
        }

        Assert.Equal(3, calls);
        Assert.False((await cache.TryGetAsync<int>("key")).HasValue);
    }

    [Fact]
    public void DisabledCache_SkipsValidationSoRedisSettingsAreNotRequired()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCachingOptions(options =>
        {
            options.Enabled = false;
            options.Mode = CacheMode.Hybrid;
            options.ApplicationPrefix = string.Empty;
        });

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IFusionCache>());
    }

    [Fact]
    public void FluentOverrides_WinOverConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheOptions:ApplicationPrefix"] = "from-config",
                ["CacheOptions:DefaultExpiration"] = "00:05:00"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, cache => cache.WithDefaultExpiration(TimeSpan.FromMinutes(30)));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CachingOptions>>().Get("default");

        Assert.Equal("from-config", options.ApplicationPrefix);
        Assert.Equal(TimeSpan.FromMinutes(30), options.DefaultExpiration);
    }

    [Fact]
    public void ValidateCachingRegistration_ResolvesEveryCache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests"));
        services.AddCaching("second", cache => cache.UseInMemory().WithApplicationPrefix("tests"));

        using var provider = services.BuildServiceProvider();
        Assert.Same(provider, provider.ValidateCachingRegistration());
    }

    [Fact]
    public void CustomKeyFactory_RegisteredBeforeAddCaching_Wins()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Keys.ICacheKeyFactory, TenantKeyFactory>();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests"));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<TenantKeyFactory>(provider.GetRequiredService<Keys.ICacheKeyFactory>());
    }

    private sealed class TenantKeyFactory : Keys.ICacheKeyFactory
    {
        public Keys.CacheKeyBuilder For<T>(object id) => Keys.CacheKey.For<T>(id).WithTenant("acme");
    }
}
