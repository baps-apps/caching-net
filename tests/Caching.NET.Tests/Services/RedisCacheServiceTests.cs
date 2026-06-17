using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Resilience;
using Caching.NET.Serialization;
using Caching.NET.Services;
using Caching.NET.Tests.Fakes;
using System.IO.Compression;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Caching.NET.Tests.Services;

public class RedisCacheServiceTests
{
    private static IServiceCollection BaseServices(IDistributedCache distributedCache, CacheOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(distributedCache);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options ?? new CacheOptions { KeyPrefix = "t" }));
        services.AddSingleton<ICacheSerializer>(new JsonCacheSerializer());
        services.AddSingleton(CacheResiliencePipelineBuilder.BuildDefaultRegistry(
            timeout: TimeSpan.FromSeconds(5), retryCount: 0));
        services.AddSingleton<RedisCacheService>();
        return services;
    }

    [Fact]
    public async Task GetOrCreateAsync_StoresAndReturnsValue_WithFakeCache()
    {
        var services = BaseServices(new FakeDistributedCache());
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        var value = await cache.GetOrCreateAsync("key1", _ => Task.FromResult("value1"));
        Assert.Equal("value1", value);

        var again = await cache.GetOrCreateAsync("key1", _ => Task.FromResult("other"));
        Assert.Equal("value1", again);
    }

    [Fact]
    public async Task GetOrCreateAsync_DoesNotWriteNullFactoryResultToBackend()
    {
        var distributed = new Mock<IDistributedCache>();
        distributed
            .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var services = BaseServices(distributed.Object);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        var first = await cache.GetOrCreateAsync("nk", _ => Task.FromResult<string>(null!));
        Assert.Null(first);

        // null must never be serialized/written to the distributed backend
        distributed.Verify(
            d => d.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenGetAsyncThrows_FailOpen_ExecutesFactory()
    {
        var mockCache = new Mock<IDistributedCache>();
        mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis down"));

        var services = BaseServices(mockCache.Object, new CacheOptions { KeyPrefix = "t", FailOpen = true });
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        var value = await cache.GetOrCreateAsync("k", _ => Task.FromResult(42));
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenGetAsyncThrows_AndFailOpenFalse_PropagatesException()
    {
        var mockCache = new Mock<IDistributedCache>();
        mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis down"));

        var services = BaseServices(mockCache.Object, new CacheOptions { KeyPrefix = "t", FailOpen = false, ThrowOnFailure = true });
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCreateAsync("k", _ => Task.FromResult(42)));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenKeyExceedsMaximumKeyLength_SkipsCacheAndRunsFactory()
    {
        var fake = new FakeDistributedCache();
        var services = BaseServices(fake, new CacheOptions { KeyPrefix = "t", MaximumKeyLength = 64 });
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        // 65-char key exceeds the limit
        var bigKey = new string('a', 65);
        var value = await cache.GetOrCreateAsync(bigKey, _ => Task.FromResult("v"));
        Assert.Equal("v", value);

        var again = await cache.GetOrCreateAsync(bigKey, _ => Task.FromResult("v2"));
        Assert.Equal("v2", again);
    }

    [Fact]
    public async Task SetAsync_RemoveAsync_Work_WithFakeCache()
    {
        var services = BaseServices(new FakeDistributedCache());
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        await cache.SetAsync("k", "data");
        var v = await cache.GetOrCreateAsync("k", _ => Task.FromResult("miss"));
        Assert.Equal("data", v);
        await cache.RemoveAsync("k");
        var after = await cache.GetOrCreateAsync("k", _ => Task.FromResult("miss"));
        Assert.Equal("miss", after);
    }

    [Fact]
    public async Task RemoveByTagAsync_IsNoOp()
    {
        var services = BaseServices(new FakeDistributedCache());
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        await cache.RemoveByTagAsync("tag");
        await cache.RemoveByTagAsync(new[] { "a", "b" });
    }

    [Fact]
    public async Task SetAsync_writes_envelope_wrapped_payload_to_distributed_cache()
    {
        var distributed = new Mock<IDistributedCache>();
        byte[]? captured = null;
        distributed
            .Setup(d => d.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, b, _, _) => captured = b)
            .Returns(Task.CompletedTask);

        var services = BaseServices(distributed.Object);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();

        await cache.SetAsync("orders-svc:Order:1", new TestDto { Id = 1 });

        Assert.NotNull(captured);
        Assert.True(captured!.Length >= PayloadEnvelope.HeaderSize);
        Assert.True(captured.AsSpan(0, 4).SequenceEqual("CN20"u8));
        Assert.Equal(PayloadEnvelope.FormatIdJson, captured[4]);
    }

    [Fact]
    public async Task SetAsync_with_compression_enabled_sets_format_compression_bit()
    {
        var distributed = new Mock<IDistributedCache>();
        byte[]? captured = null;
        distributed
            .Setup(d => d.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, b, _, _) => captured = b)
            .Returns(Task.CompletedTask);

        var services = BaseServices(
            distributed.Object,
            new CacheOptions
            {
                KeyPrefix = "t",
                EnablePayloadCompression = true,
                PayloadCompressionThresholdBytes = 256,
            });
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<RedisCacheService>();
        var big = new string('x', 5000);

        await cache.SetAsync("orders-svc:Order:2", new { Text = big });

        Assert.NotNull(captured);
        Assert.True((captured![4] & 0x80) != 0);
    }

    [Fact]
    public async Task GetOrCreateAsync_returns_factory_value_when_envelope_magic_is_invalid()
    {
        var distributed = new Mock<IDistributedCache>();
        distributed
            .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

        await using var provider = BaseServices(distributed.Object).BuildServiceProvider();
        var service = provider.GetRequiredService<RedisCacheService>();
        var result = await service.GetOrCreateAsync("k", _ => Task.FromResult(new TestDto { Id = 42 }));

        Assert.Equal(42, result.Id);
    }

    [Fact]
    public async Task GetOrCreateAsync_treats_format_drift_as_miss_and_runs_factory()
    {
        var realHash = StableTypeHash.Compute<TestDto>();
        byte[] staleWire = PayloadEnvelope.Write("{\"Id\":99}"u8.ToArray(), PayloadEnvelope.FormatIdMsgPack, realHash);
        var distributed = new Mock<IDistributedCache>();
        distributed
            .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(staleWire);

        // service uses JSON serializer — format byte in wire is MsgPack = drift
        await using var provider = BaseServices(distributed.Object).BuildServiceProvider();
        var service = provider.GetRequiredService<RedisCacheService>();
        var result = await service.GetOrCreateAsync("k", _ => Task.FromResult(new TestDto { Id = 5 }));

        Assert.Equal(5, result.Id);
    }

    [Fact]
    public async Task GetOrCreateAsync_treats_schema_drift_as_miss_and_runs_factory()
    {
        var oldHash = 0x1234_5678_9ABC_DEF0UL; // intentionally not the real TestDto hash
        var stalePayload = "{\"Id\":99}"u8.ToArray();
        byte[] staleWire = PayloadEnvelope.Write(stalePayload, PayloadEnvelope.FormatIdJson, oldHash);
        var distributed = new Mock<IDistributedCache>();
        distributed
            .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(staleWire);

        await using var provider = BaseServices(distributed.Object).BuildServiceProvider();
        var service = provider.GetRequiredService<RedisCacheService>();
        var result = await service.GetOrCreateAsync("k", _ => Task.FromResult(new TestDto { Id = 7 }));

        Assert.Equal(7, result.Id);
    }

    [Fact]
    public async Task GetOrCreateAsync_returns_cached_value_when_envelope_matches()
    {
        var realHash = StableTypeHash.Compute<TestDto>();
        byte[] wire = PayloadEnvelope.Write("{\"Id\":99}"u8, PayloadEnvelope.FormatIdJson, realHash);
        var distributed = new Mock<IDistributedCache>();
        distributed
            .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(wire);

        await using var provider = BaseServices(distributed.Object).BuildServiceProvider();
        var service = provider.GetRequiredService<RedisCacheService>();
        var result = await service.GetOrCreateAsync("k", _ => Task.FromResult(new TestDto { Id = 0 }));

        Assert.Equal(99, result.Id);
    }

    [Fact]
    public async Task GetOrCreateAsync_reads_compressed_payload_when_format_bit_is_set()
    {
        var json = "{\"Id\":123}"u8.ToArray();
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                brotli.Write(json);
            }

            compressed = output.ToArray();
        }

        var realHash = StableTypeHash.Compute<TestDto>();
        byte format = (byte)(PayloadEnvelope.FormatIdJson | 0x80);
        byte[] wire = PayloadEnvelope.Write(compressed, format, realHash);
        var distributed = new Mock<IDistributedCache>();
        distributed
            .Setup(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(wire);

        await using var provider = BaseServices(distributed.Object).BuildServiceProvider();
        var service = provider.GetRequiredService<RedisCacheService>();
        var result = await service.GetOrCreateAsync("k", _ => Task.FromResult(new TestDto { Id = 0 }));

        Assert.Equal(123, result.Id);
    }

    private sealed class TestDto { public int Id { get; init; } }
}
