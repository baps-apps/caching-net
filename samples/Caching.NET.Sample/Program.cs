using Caching.NET;
using Caching.NET.Extensions;
using Caching.NET.Telemetry;
using Caching.NET.Sample.Data;

namespace Caching.NET.Sample;

/// <summary>
/// Sample host. Everything cache-related is configured through Caching.NET: there is no cache
/// engine registration, no serializer wiring and no backplane setup anywhere in this application.
/// </summary>
public static class Program
{
    /// <summary>Builds and runs the sample web host.</summary>
    /// <param name="args">Command-line arguments.</param>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOpenApi();
        builder.Services.AddControllers();
        builder.Services.AddSingleton<ProductRepository>();

        // ------------------------------------------------------------------ the default cache
        // Bound from the "CacheOptions" section, with code-only settings layered on top.
        // Fluent settings win over configuration.
        builder.Services.AddCaching(builder.Configuration, cache => cache
            .WithEnvironmentPrefix(builder.Environment.EnvironmentName.ToLowerInvariant())
            .WithHealthChecks(splitLivenessReadiness: true));

        // ------------------------------------------------------------------ an extra named cache
        // Resolvable with [FromKeyedServices("short-lived")] or ICacheProvider.GetCache(...).
        builder.Services.AddCaching("short-lived", cache => cache
            .UseInMemory()
            .WithApplicationPrefix("sample-api")
            .WithEnvironmentPrefix(builder.Environment.EnvironmentName.ToLowerInvariant())
            .WithDefaultExpiration(TimeSpan.FromSeconds(30))
            .WithFailSafe(enabled: false));

        // ------------------------------------------------------------------ observability
        // The Caching.NET-owned instrumentation names are the only thing the host needs. Uncomment
        // once an OpenTelemetry exporter is configured:
        //
        // builder.Services.AddOpenTelemetry()
        //     .WithTracing(t => t.AddSource(CacheTelemetry.ActivitySourceNames))
        //     .WithMetrics(m => m.AddMeter(CacheTelemetry.MeterNames));
        _ = CacheTelemetry.ActivitySourceNames;

        var app = builder.Build();

        // Fails fast if cache configuration or dependency injection is wrong.
        app.Services.ValidateCachingRegistration();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("liveness")
        });
        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("readiness")
        });
        app.MapControllers();

        app.Run();
    }
}
