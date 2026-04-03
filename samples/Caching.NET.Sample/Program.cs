using Caching.NET.Extensions;

namespace Caching.NET.Sample;

/// <summary>
/// Entry point for the Caching.NET sample web application.
/// Demonstrates all supported registration patterns for Caching.NET.
/// </summary>
public class Program
{
    /// <summary>Builds and runs the sample web host.</summary>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOpenApi();

        // ────────────────────────────────────────────────────────────
        // Caching.NET Registration — pick ONE of the examples below.
        // ────────────────────────────────────────────────────────────

        // Example 1: Zero-config (InMemory, enabled, sensible defaults)
        //   No appsettings section needed. Great for prototyping or simple apps.
        //
        // builder.Services.AddCaching();

        // Example 2: Config-file driven
        //   Reads CacheOptions from appsettings.json. Existing approach — fully supported.
        //
        // builder.Services.AddCaching(builder.Configuration);

        // Example 3: Fluent code-first
        //   Programmatic configuration with IntelliSense. No config file needed.
        //
        // builder.Services.AddCaching(cache => cache
        //     .UseHybrid("localhost:6379")
        //     .WithDefaultExpiration(TimeSpan.FromMinutes(15))
        //     .WithDefaultLocalExpiration(TimeSpan.FromMinutes(5))
        //     .WithInstanceName("sampleapp:")
        //     .WithOpenTelemetry()
        //     .WithHealthChecks());

        // Example 4: Config-file + fluent overrides (recommended for production)
        //   Base configuration from appsettings.json, with fluent additions.
        //   Fluent settings override config-file values on conflict.
        builder.Services.AddCaching(builder.Configuration, cache => cache
            .WithOpenTelemetry()
            .WithHealthChecks());

        // Example 5: Explicitly disabled (for testing/staging)
        //   ICacheService still resolves — all calls pass through to factories.
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
