using Caching.NET.Extensions;

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

        // v2 default reference (when not explicitly overridden):
        // - Enabled: true
        // - Mode: InMemory
        // - KeyPrefix: REQUIRED (must be provided via config or builder)
        // - DefaultExpiration: 00:10:00
        // - TtlJitterPercentage: 0.10
        // - MaximumKeyLength: 512
        // - MaximumPayloadBytes: 1_048_576 (1 MiB)
        // - StripeLockCount: 1024
        // - StaleRefreshConcurrency: 256
        // - FactoryTimeout: 00:00:30
        // - RedisOperationTimeout: 00:00:02
        // - StrictRedisCertificateValidation: true
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
            .WithStrictCertificateValidation()      // Production-safe Redis TLS posture.

            // Feature toggles / integrations.
            .WithOpenTelemetry()                    // In v2 this is API-compatible; host OTel wiring does the work.
            .WithHealthChecks()                     // Adds CachingHealthCheck registration.
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
}
