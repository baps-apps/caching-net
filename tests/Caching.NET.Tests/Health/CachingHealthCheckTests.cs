using Caching.NET.Abstractions;
using Caching.NET.Health;
using Caching.NET.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Caching.NET.Tests.Health;

public sealed class CachingHealthCheckTests
{
    [Fact]
    public async Task RedisMode_DisconnectedMultiplexer_ReturnsUnhealthy()
    {
        var cacheMock = new Mock<ICacheService>(MockBehavior.Strict);
        var muxMock = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        muxMock.SetupGet(m => m.IsConnected).Returns(false);
        var options = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            Enabled = true,
            Mode = CacheMode.Redis,
            KeyPrefix = "test",
            RedisConnectionString = "localhost:6379",
            FailOpen = true
        });
        var health = new CachingHealthCheck(cacheMock.Object, options, NullLogger<CachingHealthCheck>.Instance, muxMock.Object);

        var result = await health.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("cache", health, HealthStatus.Unhealthy, tags: null)
        });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task InMemoryMode_UsesProbeCacheCall_AndReturnsHealthy()
    {
        var cacheMock = new Mock<ICacheService>(MockBehavior.Strict);
        cacheMock
            .Setup(c => c.ExistsAsync(
                It.Is<string>(k => k.Contains("caching-net:health:probe:", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        cacheMock
            .Setup(c => c.GetOrCreateAsync(
                It.Is<string>(k => k.Contains("caching-net:health:probe:", StringComparison.Ordinal)),
                It.IsAny<Func<CancellationToken, Task<bool>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var options = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            Enabled = true,
            Mode = CacheMode.InMemory,
            KeyPrefix = "test"
        });
        var health = new CachingHealthCheck(cacheMock.Object, options, NullLogger<CachingHealthCheck>.Instance, multiplexer: null);

        var result = await health.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("cache", health, HealthStatus.Unhealthy, tags: null)
        });

        Assert.Equal(HealthStatus.Healthy, result.Status);
        cacheMock.VerifyAll();
    }

    [Fact]
    public async Task Liveness_WhenCanceled_ReturnsFailureStatus()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            Enabled = true,
            Mode = CacheMode.InMemory,
            KeyPrefix = "test",
        });
        var health = new CachingLivenessHealthCheck(options, multiplexer: null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await health.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("live", health, HealthStatus.Unhealthy, tags: null)
        }, cts.Token);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Liveness_RedisMode_DisconnectedMultiplexer_ReturnsUnhealthy()
    {
        var muxMock = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        muxMock.SetupGet(m => m.IsConnected).Returns(false);
        var options = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            Enabled = true,
            Mode = CacheMode.Redis,
            KeyPrefix = "test",
            RedisConnectionString = "localhost:6379",
        });
        var health = new CachingLivenessHealthCheck(options, muxMock.Object);

        var result = await health.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("live", health, HealthStatus.Unhealthy, tags: null)
        });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Liveness_InMemory_ReturnsHealthy_WithoutTouchingCacheService()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            Enabled = true,
            Mode = CacheMode.InMemory,
            KeyPrefix = "test",
        });
        var health = new CachingLivenessHealthCheck(options, multiplexer: null);

        var result = await health.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("live", health, HealthStatus.Unhealthy, tags: null)
        });

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
