using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Resilience;
using Caching.NET.Serialization;
using Caching.NET.Services;
using Caching.NET.Tests.Fakes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Caching.NET.Tests.Services;

/// <summary>
/// Behavioral parity tests for the runtime-typed <c>GetAsync(string, Type, CancellationToken)</c>
/// overload across all implementations (§7 of the design spec).
/// </summary>
public class RuntimeTypedGetAsyncTests
{
    public sealed record Foo(int Id, string Name);

    private static IServiceProvider RoutingProvider(string mode, bool enabled = true)
    {
        var config = new Dictionary<string, string?>
        {
            ["CacheOptions:Enabled"] = enabled ? "true" : "false",
            ["CacheOptions:Mode"] = mode,
            ["CacheOptions:KeyPrefix"] = "test",
            ["CacheOptions:RedisConnectionString"] = "localhost:6379",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration);
        return services.BuildServiceProvider();
    }

    private static RedisCacheService RedisService(IDistributedCache distributed, CacheOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(distributed);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options ?? new CacheOptions { KeyPrefix = "t" }));
        services.AddSingleton<ICacheSerializer>(new JsonCacheSerializer());
        services.AddSingleton(CacheResiliencePipelineBuilder.BuildDefaultRegistry(timeout: TimeSpan.FromSeconds(5), retryCount: 0));
        services.AddSingleton<RedisCacheService>();
        return services.BuildServiceProvider().GetRequiredService<RedisCacheService>();
    }

    // ---- Redis ----

    [Fact]
    public async Task Redis_RoundTrip_Parity_Generic_And_Runtime()
    {
        var redis = RedisService(new FakeDistributedCache());
        var foo = new Foo(1, "alpha");
        await redis.SetAsync("k", foo);

        var generic = await redis.GetAsync<Foo>("k");
        var runtime = await redis.GetAsync("k", typeof(Foo));

        Assert.Equal(foo, generic);
        Assert.Equal(foo, Assert.IsType<Foo>(runtime));
        Assert.Equal(generic, (Foo?)runtime);
    }

    [Fact]
    public async Task Redis_Reverse_Parity_RuntimeRead_Matches_GenericRead()
    {
        var redis = RedisService(new FakeDistributedCache());
        var foo = new Foo(2, "beta");
        await redis.SetAsync("k", foo);

        var runtime = (Foo?)await redis.GetAsync("k", typeof(Foo));
        var generic = await redis.GetAsync<Foo>("k");

        Assert.Equal(generic, runtime);
    }

    [Fact]
    public async Task Redis_Miss_Returns_Null()
    {
        var redis = RedisService(new FakeDistributedCache());
        Assert.Null(await redis.GetAsync("absent", typeof(Foo)));
    }

    [Fact]
    public async Task Redis_SchemaDrift_Returns_Null()
    {
        var staleWire = PayloadEnvelope.Write("{\"Id\":9,\"Name\":\"x\"}"u8.ToArray(), PayloadEnvelope.FormatIdJson, schemaHash: 0xDEAD_BEEF_DEAD_BEEFUL);
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(staleWire);

        var redis = RedisService(distributed.Object);
        Assert.Null(await redis.GetAsync("k", typeof(Foo)));
    }

    [Fact]
    public async Task Redis_FormatDrift_Returns_Null()
    {
        // Written with MessagePack format byte but the configured serializer is JSON.
        var realHash = StableTypeHash.Compute(typeof(Foo));
        var staleWire = PayloadEnvelope.Write("{\"Id\":9,\"Name\":\"x\"}"u8.ToArray(), PayloadEnvelope.FormatIdMsgPack, realHash);
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(staleWire);

        var redis = RedisService(distributed.Object);
        Assert.Null(await redis.GetAsync("k", typeof(Foo)));
    }

    [Fact]
    public async Task Redis_NullType_Throws()
    {
        var redis = RedisService(new FakeDistributedCache());
        await Assert.ThrowsAsync<ArgumentNullException>(() => redis.GetAsync("k", null!));
    }

    // ---- InMemory ----

    [Fact]
    public async Task InMemory_RoundTrip_Parity()
    {
        await using var provider = (ServiceProvider)RoutingProvider("InMemory");
        var cache = provider.GetRequiredService<ICacheService>();
        var foo = new Foo(3, "gamma");
        await cache.SetAsync("k", foo);

        var generic = await cache.GetAsync<Foo>("k");
        var runtime = (Foo?)await cache.GetAsync("k", typeof(Foo));

        Assert.Equal(foo, generic);
        Assert.Equal(generic, runtime);
    }

    [Fact]
    public async Task InMemory_TypeMismatch_Is_Miss()
    {
        await using var provider = (ServiceProvider)RoutingProvider("InMemory");
        var cache = provider.GetRequiredService<ICacheService>();
        await cache.SetAsync("k", new Foo(4, "delta"));

        Assert.Null(await cache.GetAsync("k", typeof(string)));
    }

    // ---- Hybrid ----

    [Fact]
    public async Task Hybrid_RoundTrip_Parity()
    {
        await using var provider = (ServiceProvider)RoutingProvider("Hybrid");
        var cache = provider.GetRequiredService<ICacheService>();
        var key = $"hybrid:{Guid.NewGuid():N}";
        var foo = new Foo(5, "epsilon");
        // Populate via GetOrCreateAsync (the reliable Hybrid write path without a live Redis L2).
        await cache.GetOrCreateAsync(key, _ => Task.FromResult(foo));

        var generic = await cache.GetAsync<Foo>(key);
        var runtime = (Foo?)await cache.GetAsync(key, typeof(Foo));

        Assert.Equal(foo, generic);
        Assert.Equal(generic, runtime);
    }

    [Fact]
    public async Task Hybrid_Miss_Returns_Null()
    {
        await using var provider = (ServiceProvider)RoutingProvider("Hybrid");
        var cache = provider.GetRequiredService<ICacheService>();
        Assert.Null(await cache.GetAsync($"absent:{Guid.NewGuid():N}", typeof(Foo)));
    }

    // ---- Routing ----

    [Fact]
    public async Task Routing_NullType_Throws()
    {
        await using var provider = (ServiceProvider)RoutingProvider("InMemory");
        var cache = provider.GetRequiredService<ICacheService>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetAsync("k", null!));
    }

    [Fact]
    public async Task Routing_Disabled_Returns_Null()
    {
        await using var provider = (ServiceProvider)RoutingProvider("InMemory", enabled: false);
        var cache = provider.GetRequiredService<ICacheService>();
        Assert.Null(await cache.GetAsync("k", typeof(Foo)));
    }

    // ---- Default interface method fallback for external implementations ----

    [Fact]
    public async Task DefaultInterfaceMethod_Falls_Back_To_Generic_For_Custom_Implementations()
    {
        ICacheService custom = new GenericOnlyCacheService();
        var result = await custom.GetAsync("k", typeof(Foo));
        Assert.Equal(new Foo(42, "fallback"), Assert.IsType<Foo>(result));
    }

    [Fact]
    public async Task DefaultInterfaceMethod_Returns_Null_On_Generic_Miss()
    {
        ICacheService custom = new GenericOnlyCacheService(returnNull: true);
        Assert.Null(await custom.GetAsync("k", typeof(Foo)));
    }

    /// <summary>
    /// Minimal external-style implementation that only provides the generic <c>GetAsync&lt;T&gt;</c>,
    /// relying on the default interface method for the runtime-typed overload.
    /// </summary>
    private sealed class GenericOnlyCacheService(bool returnNull = false) : ICacheService
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : notnull
            => Task.FromResult(returnNull ? default : (T?)(object)new Foo(42, "fallback"));

        public Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull => factory(cancellationToken);
        public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull => Task.CompletedTask;
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task RefreshAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default) where T : notnull => Task.FromResult<IReadOnlyDictionary<string, T?>>(new Dictionary<string, T?>());
        public Task SetManyAsync<T>(IReadOnlyDictionary<string, T> items, TimeSpan? expiration = null, TimeSpan? localExpiration = null, CancellationToken cancellationToken = default) where T : notnull => Task.CompletedTask;
        public Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
