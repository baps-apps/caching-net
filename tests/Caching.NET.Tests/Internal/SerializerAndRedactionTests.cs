using System.Text;
using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using ZiggyCreatures.Caching.Fusion.Serialization;
using ZiggyCreatures.Caching.Fusion.Serialization.NeueccMessagePack;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Caching.NET.Tests.Internal;

[Collection(Telemetry.MetricsCollection.Name)]
public class SerializerAndRedactionTests
{
    public sealed record Payload(int Id, string Name);

    private static InstrumentedCacheSerializer Build(
        IFusionCacheSerializer inner,
        Action<CacheSerializationOptions>? configure = null)
    {
        var options = new CachingOptions { CacheName = "default", ApplicationPrefix = "tests" };
        configure?.Invoke(options.Serialization);
        return new InstrumentedCacheSerializer(
            inner,
            options.Serialization,
            "default",
            new CacheTelemetryContext(options),
            NullLogger.Instance);
    }

    [Fact]
    public async Task JsonSerializer_RoundTripsThroughTheFramingLayer()
    {
        var serializer = Build(new FusionCacheSystemTextJsonSerializer());
        var value = new Payload(1, "widget");

        var bytes = await serializer.SerializeAsync(value);
        var restored = await serializer.DeserializeAsync<Payload>(bytes);

        Assert.Equal(value, restored);
    }

    [Fact]
    public void JsonSerializer_RoundTripsSynchronously()
    {
        var serializer = Build(new FusionCacheSystemTextJsonSerializer());
        var value = new Payload(2, "gadget");

        Assert.Equal(value, serializer.Deserialize<Payload>(serializer.Serialize(value)));
    }

    [Fact]
    public async Task MessagePackSerializer_RoundTrips()
    {
        var serializer = Build(new FusionCacheNeueccMessagePackSerializer());
        var value = new Payload(3, "gizmo");

        var bytes = await serializer.SerializeAsync(value);

        Assert.Equal(value, await serializer.DeserializeAsync<Payload>(bytes));
    }

    [Fact]
    public async Task CompressionEnabled_ShrinksLargePayloadsAndStillRoundTrips()
    {
        var serializer = Build(
            new FusionCacheSystemTextJsonSerializer(),
            s =>
            {
                s.Compression.Enabled = true;
                s.Compression.ThresholdBytes = 128;
                s.MaximumPayloadBytes = 10_000_000;
            });

        var value = new Payload(4, new string('x', 100_000));

        var bytes = await serializer.SerializeAsync(value);

        Assert.True(bytes.Length < 10_000);
        Assert.Equal(value, await serializer.DeserializeAsync<Payload>(bytes));
    }

    [Fact]
    public async Task OversizedPayload_IsRefusedOnWrite()
    {
        var serializer = Build(new FusionCacheSystemTextJsonSerializer(), s => s.MaximumPayloadBytes = 256);
        var value = new Payload(5, new string('y', 5_000));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await serializer.SerializeAsync(value));

        Assert.Contains("MaximumPayloadBytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedStoredPayload_IsRefusedOnRead()
    {
        var serializer = Build(new FusionCacheSystemTextJsonSerializer(), s => s.MaximumPayloadBytes = 32);
        var poisoned = new byte[4096];

        Assert.Throws<PayloadCodec.CorruptPayloadException>(() => serializer.Deserialize<Payload>(poisoned));
    }

    [Fact]
    public void CorruptStoredPayload_IsRefusedRatherThanDeserialized()
    {
        var serializer = Build(new FusionCacheSystemTextJsonSerializer());
        var poisoned = Encoding.UTF8.GetBytes("not a framed caching.net payload");

        Assert.Throws<PayloadCodec.CorruptPayloadException>(() => serializer.Deserialize<Payload>(poisoned));
    }

    [Fact]
    public async Task Serialization_RecordsDurationAndPayloadSizeMetrics()
    {
        using var collector = new Telemetry.MetricCollector();
        var serializer = Build(new FusionCacheSystemTextJsonSerializer());

        await serializer.SerializeAsync(new Payload(6, "metric"));

        Assert.Contains(collector.Measurements, m => m.Instrument == "caching.net.serialization.duration");
        Assert.Contains(collector.Measurements, m => m.Instrument == "caching.net.payload.size");
    }

    [Theory]
    [InlineData("host:6379,password=hunter2", "password=***")]
    [InlineData("host:6379,user=admin,password=hunter2", "user=***")]
    [InlineData("host:6379,ssl=true", "ssl=true")]
    [InlineData(null, "")]
    public void ConnectionStringRedactor_RemovesCredentials(string? input, string expectedFragment)
    {
        var redacted = RedisConnectionStringRedactor.Redact(input);

        Assert.Contains(expectedFragment, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyFingerprint_IsDeterministicAndFixedWidth()
    {
        Assert.Equal(KeyFingerprint.Compute("Order:1"), KeyFingerprint.Compute("Order:1"));
        Assert.NotEqual(KeyFingerprint.Compute("Order:1"), KeyFingerprint.Compute("Order:2"));
        Assert.Equal(16, KeyFingerprint.Compute(string.Empty).Length);
        Assert.Equal(16, KeyFingerprint.Compute(new string('k', 5000)).Length);
    }
}
