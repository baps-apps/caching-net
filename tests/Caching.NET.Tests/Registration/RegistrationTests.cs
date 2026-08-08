using Caching.NET;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caching.NET.Tests.Registration;

public class RegistrationTests
{
    [Fact]
    public void DefaultRegistration_ResolvesTheCacheOperationContract()
    {
        using var provider = TestHost.BuildInMemory();

        // No Assert.NotNull: GetRequiredService throws or returns non-null, so it could not fail.
        var cache = provider.GetRequiredService<ICacheService>();

        Assert.Equal(CachingDefaults.DefaultCacheName, cache.CacheName);
    }

    /// <summary>
    /// The application's configuration lambda runs <b>once</b> per registration. It used to run twice:
    /// once in <c>PostConfigure</c> against the real options, and once eagerly against a throwaway
    /// <c>CachingOptions</c> purely to read whether <c>WithHealthChecks()</c> had been called. Every
    /// side effect in the lambda double-ran — <c>WithRedis(r =&gt; r.ClientCertificate =
    /// X509CertificateLoader.LoadPkcs12FromFile(...))</c> loaded two certificates and attached one to
    /// an options object that is discarded and never disposed; a lambda reading a secret from a vault
    /// made the call twice at startup.
    /// </summary>
    [Fact]
    public void ConfigureDelegate_RunsExactlyOncePerRegistration()
    {
        var invocations = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache =>
        {
            invocations++;
            cache.UseInMemory().WithApplicationPrefix("tests");
        });

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ICacheService>();

        Assert.Equal(1, invocations);
    }

    /// <summary>
    /// Same invariant with health checks opted in, which is the path that used to do the second
    /// replay — and which now reads the intent back from the single replay instead. Resolving
    /// <c>HealthCheckService</c> is what forces that read, so it must not run the lambda again.
    /// </summary>
    [Fact]
    public void ConfigureDelegate_RunsExactlyOnceEvenWhenHealthChecksAreRegistered()
    {
        var invocations = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache =>
        {
            invocations++;
            cache.UseInMemory().WithApplicationPrefix("tests").WithHealthChecks(splitLivenessReadiness: true);
        });

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ICacheService>();
        var registrations = provider
            .GetRequiredService<IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>()
            .Value.Registrations;

        Assert.Equal(1, invocations);
        Assert.Contains(registrations, r => r.Name == "caching-net-liveness");
        Assert.Contains(registrations, r => r.Name == "caching-net-readiness");
    }

    /// <summary>
    /// The health-check intent must survive the order in which the container happens to materialise
    /// things: here nothing resolves the cache first, so building HealthCheckServiceOptions is what
    /// forces the options — and therefore the single replay — to run.
    /// </summary>
    [Fact]
    public void HealthCheckIntent_IsReadEvenWhenTheCacheIsNeverResolved()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests").WithHealthChecks("probe"));

        using var provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>()
            .Value.Registrations;

        Assert.Contains(registrations, r => r.Name == "probe");
    }

    /// <summary>A cache that never asks for health checks must not get any.</summary>
    [Fact]
    public void WithoutWithHealthChecks_NoCachingHealthCheckIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests"));

        using var provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>()
            .Value.Registrations;

        Assert.Empty(registrations);
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
        Assert.NotNull(provider.GetRequiredService<ICacheService>());
        Assert.NotNull(provider.GetRequiredService<ICacheProvider>());
        Assert.NotNull(provider.GetRequiredService<ICacheGuard>());
    }

    [Fact]
    public void CacheServices_AreSingletons()
    {
        using var provider = TestHost.BuildInMemory();

        Assert.Same(provider.GetRequiredService<ICacheService>(), provider.GetRequiredService<ICacheService>());
        Assert.Same(provider.GetRequiredService<ICacheProvider>(), provider.GetRequiredService<ICacheProvider>());
        Assert.Same(provider.GetRequiredService<ICacheGuard>(), provider.GetRequiredService<ICacheGuard>());
    }

    [Fact]
    public void CacheResolvedFromAScope_IsTheSameSingletonInstance()
    {
        using var provider = TestHost.BuildInMemory();
        var root = provider.GetRequiredService<ICacheService>();

        using var scope = provider.CreateScope();
        var scoped = scope.ServiceProvider.GetRequiredService<ICacheService>();

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
        Assert.Same(caches.GetCache("short-lived"), provider.GetRequiredKeyedService<ICacheService>("short-lived"));
        Assert.Same(caches.Default, provider.GetRequiredService<ICacheService>());
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

    // Disabled-cache behaviour (factory on every call, nothing cached) is covered by
    // NullCacheServiceTests.FactoryRunsOnEveryCall and NullCacheServiceTests.ReadsAlwaysMiss.

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

        // Resolving is the assertion — a validated Hybrid registration with no Redis.Configuration
        // throws here. What comes back must also actually be the disabled cache rather than a
        // Hybrid one built against a missing connection, so the write-then-miss pins that too.
        var cache = provider.GetRequiredService<ICacheService>();
        cache.Set("k", 1);

        Assert.False(cache.TryGet<int>("k").HasValue);
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

    [Fact]
    public void EngineCacheIsNotResolvable()
    {
        using var host = TestHost.BuildInMemory();

        Assert.Null(host.GetService<ZiggyCreatures.Caching.Fusion.IFusionCache>());
    }

    [Fact]
    public void DefaultCacheResolvesAsICacheService()
    {
        using var host = TestHost.BuildInMemory();

        var cache = host.GetRequiredService<ICacheService>();

        Assert.Equal(CachingDefaults.DefaultCacheName, cache.CacheName);
    }

    [Fact]
    public void NamedCacheResolvesByKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(c => c.UseInMemory().WithApplicationPrefix("tests"));
        services.AddCaching("hot", c => c.UseInMemory().WithApplicationPrefix("tests"));
        using var host = services.BuildServiceProvider();

        Assert.Equal("hot", host.GetRequiredKeyedService<ICacheService>("hot").CacheName);
        Assert.Same(
            host.GetRequiredService<ICacheService>(),
            host.GetRequiredService<ICacheProvider>().Default);
    }

    [Fact]
    public void ProviderReturnsCacheServices()
    {
        using var host = TestHost.BuildInMemory();
        var provider = host.GetRequiredService<ICacheProvider>();

        Assert.NotNull(provider.Default);
        Assert.NotNull(provider.GetCache(CachingDefaults.DefaultCacheName));
        Assert.Null(provider.GetCacheOrNull("absent"));
    }
}
