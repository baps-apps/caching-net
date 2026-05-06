using Testcontainers.Redis;
using Xunit;

namespace Caching.NET.Tests.Integration.Fixtures;

public sealed class RedisContainerFixture : IAsyncLifetime
{
    public RedisContainer Container { get; }
    public string ConnectionString => Container.GetConnectionString();

    public RedisContainerFixture()
    {
        Container = new RedisBuilder().WithImage("redis:7.2-alpine").Build();
    }

    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

[CollectionDefinition("Redis")]
public sealed class RedisCollection : ICollectionFixture<RedisContainerFixture> { }
