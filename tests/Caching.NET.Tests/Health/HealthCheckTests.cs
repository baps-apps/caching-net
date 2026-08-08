using Caching.NET.Extensions;
using Caching.NET.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Caching.NET.Tests.Health;

public class HealthCheckTests
{
    [Fact]
    public async Task ReadinessCheck_IsHealthyForAWorkingInMemoryCache()
    {
        using var host = TestHost.BuildInMemory();
        var check = new CachingHealthCheck(
            host.GetRequiredService<ICacheProvider>(),
            host.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Options.CachingOptions>>());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("InMemory", result.Data["default"]);
    }

    [Fact]
    public async Task ReadinessCheck_StillReadsFromMemoryWhenThereIsNoDistributedLayer()
    {
        // The probe bypasses the memory layer only when a distributed layer exists. In InMemory mode
        // skipping it would turn every probe into a round-trip mismatch.
        using var host = TestHost.Build(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithDefaultExpiration(TimeSpan.FromHours(1)));

        var check = new CachingHealthCheck(
            host.GetRequiredService<ICacheProvider>(),
            host.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Options.CachingOptions>>());

        for (var i = 0; i < 25; i++)
        {
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
    }

    [Fact]
    public async Task ReadinessCheck_ReportsADisabledCacheWithoutProbingIt()
    {
        using var host = TestHost.Build(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .Disable());

        var check = new CachingHealthCheck(
            host.GetRequiredService<ICacheProvider>(),
            host.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Options.CachingOptions>>());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("disabled", result.Data["default"]);
    }

    [Fact]
    public async Task LivenessCheck_PerformsNoCacheIo()
    {
        using var host = TestHost.BuildInMemory();
        var check = new CachingLivenessHealthCheck(host.GetRequiredService<ICacheProvider>());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("default", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthChecks_AreRegisteredThroughTheBuilder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("tests")
            .WithHealthChecks(splitLivenessReadiness: true));

        using var provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        Assert.Contains(registrations, r => r.Name == "caching-net-liveness");
        Assert.Contains(registrations, r => r.Name == "caching-net-readiness");
    }

    /// <summary>
    /// Two caches that both opt in used to crash the application at startup with
    /// <c>ArgumentException: Duplicate health checks were registered with the name(s): caching-net</c>
    /// — a message naming neither Caching.NET nor the cause. The second registration is redundant as
    /// well as fatal: one <see cref="CachingHealthCheck"/> already probes every registered cache
    /// (see <see cref="ReadinessCheck_CoversEveryRegisteredCache"/>), so a repeat under a name that is
    /// already claimed is a no-op.
    /// </summary>
    [Fact]
    public async Task TwoCachesBothOptingIntoHealthChecks_RegisterOneCheckAndStartUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests").WithHealthChecks());
        services.AddCaching("second", cache => cache.UseInMemory().WithApplicationPrefix("tests").WithHealthChecks());

        using var provider = services.BuildServiceProvider();

        var registrations = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;
        Assert.Single(registrations, r => r.Name == "caching-net");

        // Resolving the service is what threw: it rejects duplicate names.
        var healthCheckService = provider.GetRequiredService<HealthCheckService>();
        var report = await healthCheckService.CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);

        // The single check still covers both caches.
        var entry = report.Entries["caching-net"];
        Assert.True(entry.Data.ContainsKey("default"));
        Assert.True(entry.Data.ContainsKey("second"));
    }

    /// <summary>The same guard on the public builder extension, which a consumer can call twice.</summary>
    [Fact]
    public void AddCachingHealthChecksTwiceUnderTheSameName_RegistersOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests"));
        services.AddHealthChecks().AddCachingHealthChecks();
        services.AddHealthChecks().AddCachingHealthChecks();

        using var provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        Assert.Single(registrations, r => r.Name == "caching-net");
    }

    [Fact]
    public async Task ReadinessCheck_CoversEveryRegisteredCache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("tests"));
        services.AddCaching("second", cache => cache.UseInMemory().WithApplicationPrefix("tests"));

        using var provider = services.BuildServiceProvider();
        var check = new CachingHealthCheck(
            provider.GetRequiredService<ICacheProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Options.CachingOptions>>());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True(result.Data.ContainsKey("default"));
        Assert.True(result.Data.ContainsKey("second"));
    }
}
