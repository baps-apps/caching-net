using Caching.NET.Tests.Integration.Helpers;
using Testcontainers.Redis;
using Xunit;

namespace Caching.NET.Tests.Integration;

public class RedisConnectionDropTests : IAsyncLifetime
{
    private readonly RedisContainer _container =
        new RedisBuilder().WithImage("redis:7.2-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task Container_restart_recovers_with_FailOpen()
    {
        var connString = _container.GetConnectionString();
        var (sp, cache) = IntegrationServiceProvider.Build(connString, "rt-drop");
        await using var _ = (Microsoft.Extensions.DependencyInjection.ServiceProvider)sp;

        await cache.SetAsync("k", "v");
        Assert.Equal("v", await cache.GetAsync<string>("k"));

        await _container.StopAsync();
        // Cache backend is gone — FailOpen (default true) lets the factory run.
        var got = await cache.GetOrCreateAsync("k", _ => Task.FromResult("factory"));
        Assert.Equal("factory", got);

        await _container.StartAsync();
    }
}
