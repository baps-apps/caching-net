using Caching.NET.Extensions;
using Caching.NET.Keys;
using Caching.NET.Options;
using System.Text.Json;

namespace Caching.NET.Sample;

/// <summary>
/// Entry point for the Caching.NET sample web application.
/// Demonstrates all supported registration patterns and builder options for Caching.NET.
/// </summary>
public class Program
{
    /// <summary>Builds and runs the sample web host.</summary>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOpenApi();
        builder.Services.Configure<CacheSerializerOptions>(o =>
        {
            o.JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        });
        // Register custom key factory BEFORE AddCaching so TryAddSingleton keeps this one.
        builder.Services.AddSingleton<ICacheKeyFactory, SampleCacheKeyFactory>();

        // v2 default reference (when not explicitly overridden):
        // - Enabled: true
        // - Mode: InMemory
        // - KeyPrefix: REQUIRED (must be provided via config or builder); ':' not allowed inside prefix
        // - DefaultExpiration: 00:10:00
        // - TtlJitterPercentage: 0.10
        // - MaximumKeyLength: 512
        // - MaximumPayloadBytes: 1_048_576 (1 MiB)
        // - StripeLockCount: 1024
        // - StaleRefreshConcurrency: 256
        // - FactoryTimeout: 00:00:30
        // - RedisOperationTimeout: 00:00:02
        // - StrictRedisCertificateValidation: true (sample overrides with WithPermissiveRedisTls for dev DNS→ElastiCache)
        // - FailOpen: true
        //
        // Note: RedisConnectionString is required when Mode is Redis/Hybrid.
        // Note: HybridLocalCacheExpiration and MemorySizeLimitMb are optional.

        // Recommended v2 setup: appsettings baseline + fluent overrides.
        // This sample intentionally shows all high-impact options with comments
        // so teams can understand operational trade-offs quickly.
        builder.Services.AddCaching(builder.Configuration, cache => cache
            // Mode + backend (for this sample we use Hybrid with Redis).
            .UseHybrid(builder.Configuration["CacheOptions:RedisConnectionString"] ?? "localhost:6379")
            .WithKeyPrefix("sample-api-dev") // isolation in shared Redis clusters.

            // TTL / freshness behavior.
            .WithDefaultExpiration(TimeSpan.FromMinutes(10))     // Primary cache lifetime.
            .WithDefaultLocalExpiration(TimeSpan.FromMinutes(3)) // Hybrid local (L1) lifetime.
            .WithTtlJitter(0.10)                                 // +/-10% expiry spread to reduce herd effects.

            // Capacity / limits.
            .WithMaximumKeyLength(512)             // Prevents pathological key growth.
            .WithMaximumPayloadBytes(1_048_576)    // 1MiB guardrail against oversized objects.
            .WithMemorySizeLimit(256)              // L1 cache cap in MB.
            .WithStripedLocks(2048)                // Higher concurrency for hot-key workloads.

            // Timeout / resilience tuning.
            .WithFactoryTimeout(TimeSpan.FromSeconds(30))
            .WithRedisOperationTimeout(TimeSpan.FromSeconds(2))
            .WithStaleRefreshConcurrency(256)      // Bound SWR background refresh pressure.
            .WithPermissiveRedisTls()               // Match legacy Allow name mismatch for custom host→ElastiCache; use strict in prod.
            .WithKeyTransformer(key => key.Trim().ToLowerInvariant())
            .WithKeyValidator(key => key.Length <= 128)
            .WithResilience(resilience =>
            {
                resilience.Timeout = TimeSpan.FromSeconds(2);
                resilience.RetryCount = 2;
            })

            // Feature toggles / integrations.
            .WithOpenTelemetry()                    // In v2 this is API-compatible; host OTel wiring does the work.
            .WithHealthChecks(splitLivenessReadiness: true)
            .RequireTagSupport());                  // Enforces Hybrid mode for tag invalidation paths.

        // Optional: use MessagePack serializer when payload size/CPU profile benefits from binary format.
        // builder.Services.AddCaching(cache => cache.WithMessagePackSerializer());

        builder.Services.AddControllers();

        var app = builder.Build();

        // Validate cache registration at startup (fails fast on DI misconfiguration)
        app.Services.ValidateCacheRegistration();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();
        app.MapHealthChecks("/health");
        app.MapControllers();

        app.Run();
    }

    private sealed class SampleCacheKeyFactory : ICacheKeyFactory
    {
        public CacheKeyBuilder For<T>(object id) => CacheKey.For<T>(id).WithSegment("tenant:sample");
    }
}
