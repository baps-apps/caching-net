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

        // ════════════════════════════════════════════════════════════
        // Caching.NET Registration — pick ONE of the examples below.
        // ════════════════════════════════════════════════════════════

        // ── Example 1: Zero-config ─────────────────────────────────
        //   Hybrid mode (in-memory only), enabled, 10-minute default expiration.
        //   No appsettings section needed. Great for prototyping or simple apps.
        //
        // builder.Services.AddCaching();

        // ── Example 2: Config-file driven ──────────────────────────
        //   Reads CacheOptions section from appsettings.json.
        //
        // builder.Services.AddCaching(builder.Configuration);

        // ── Example 3: InMemory with all builder options ───────────
        //   Programmatic configuration with IntelliSense. No config file needed.
        //
        // builder.Services.AddCaching(cache => cache
        //     .UseInMemory()
        //     .WithDefaultExpiration(TimeSpan.FromMinutes(15))
        //     .WithMemorySizeLimit(256)                        // cap IMemoryCache at 256 MB
        //     .WithMaximumKeyLength(512)                       // reject keys longer than 512 chars
        //     .WithFactoryTimeout(TimeSpan.FromSeconds(30))    // cancel slow factories after 30s
        //     .WithOpenTelemetry()
        //     .WithHealthChecks());

        // ── Example 4: Redis with connection string ────────────────
        //   Distributed cache backed by Redis. Includes payload limits and TLS.
        //
        // builder.Services.AddCaching(cache => cache
        //     .UseRedis("localhost:6379,abortConnect=false")
        //     .WithKeyPrefix("sampleapp:")                  // key prefix for multi-service clusters
        //     .WithDefaultExpiration(TimeSpan.FromMinutes(10))
        //     .WithMaximumPayloadBytes(5_000_000)              // skip caching entries > 5 MB
        //     .WithMaximumKeyLength(1024)
        //     .WithStrictCertificateValidation()               // enforce strict TLS in production
        //     .WithOpenTelemetry()
        //     .WithHealthChecks());

        // ── Example 5: Redis with programmatic ConfigurationOptions ─
        //   Full control over StackExchange.Redis settings.
        //
        // builder.Services.AddCaching(cache => cache
        //     .UseRedis(redis =>
        //     {
        //         redis.EndPoints.Add("redis-primary", 6379);
        //         redis.EndPoints.Add("redis-replica", 6380);
        //         redis.Password = Environment.GetEnvironmentVariable("REDIS_PASSWORD");
        //         redis.ConnectTimeout = 5000;
        //         redis.SyncTimeout = 3000;
        //         redis.AbortOnConnectFail = false;
        //     })
        //     .WithKeyPrefix("sampleapp:")
        //     .WithOpenTelemetry());

        // ── Example 6: Hybrid (in-memory + Redis) ──────────────────
        //   Two-tier cache with stampede protection. Best for production.
        //
        // builder.Services.AddCaching(cache => cache
        //     .UseHybrid("localhost:6379")
        //     .WithDefaultExpiration(TimeSpan.FromMinutes(15))       // distributed tier TTL
        //     .WithDefaultLocalExpiration(TimeSpan.FromMinutes(5))   // in-memory tier TTL
        //     .WithKeyPrefix("sampleapp:")
        //     .WithMemorySizeLimit(128)
        //     .WithMaximumPayloadBytes(10_000_000)
        //     .WithFactoryTimeout(TimeSpan.FromSeconds(30))
        //     .WithOpenTelemetry()
        //     .WithHealthChecks());

        // ── Example 7: Hybrid (in-memory only, no Redis) ───────────
        //   Stampede protection without a Redis dependency.
        //
        // builder.Services.AddCaching(cache => cache
        //     .UseHybrid()
        //     .WithDefaultExpiration(TimeSpan.FromMinutes(10))
        //     .WithOpenTelemetry());

        // ── Example 8: Config-file + fluent overrides (recommended) ─
        //   Base configuration from appsettings.json, with fluent additions.
        //   Fluent settings override config-file values on conflict.
        builder.Services.AddCaching(builder.Configuration, cache => cache
            .WithOpenTelemetry()
            .WithHealthChecks()
            .WithTtlJitter(0.10)
            .WithStaleRefreshConcurrency(128));

        // ── Example 9: Explicitly disabled ─────────────────────────
        //   ICacheService still resolves — all calls pass through to factories.
        //   Useful for testing or staging environments.
        //
        // builder.Services.AddCaching(cache => cache.Disable());

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
