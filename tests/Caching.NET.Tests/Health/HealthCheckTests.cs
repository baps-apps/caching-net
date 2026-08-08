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
