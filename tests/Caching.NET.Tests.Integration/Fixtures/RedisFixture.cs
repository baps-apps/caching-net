using Testcontainers.Redis;

namespace Caching.NET.Tests.Integration.Fixtures;

/// <summary>
/// A single Redis container shared by every integration test class. Tests isolate themselves from
/// each other with distinct application prefixes rather than by starting a container each.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4-alpine").Build();

    public string ConnectionString => $"{_container.GetConnectionString()},abortConnect=false";

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
