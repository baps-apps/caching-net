using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;

namespace Caching.NET.Sample;

/// <summary>
/// Entry point for the Caching.NET sample web application.
/// Demonstrates how to wire up Caching.NET in an ASP.NET Core host using
/// <c>AddCaching</c>, health checks, and optional OpenTelemetry telemetry.
/// </summary>
public class Program
{
    /// <summary>Builds and runs the sample web host.</summary>
    /// <param name="args">Command-line arguments forwarded to <see cref="WebApplication.CreateBuilder(string[])"/>.</param>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        // Register Caching.NET using configuration-bound CacheOptions
        builder.Services.AddCaching(builder.Configuration);

        // Optionally enable OpenTelemetry-style cache telemetry by registering
        // an ICacheTelemetry implementation before or after AddCaching, e.g.:
        //
        // builder.Services.AddSingleton<ICacheTelemetry, Caching.NET.Telemetry.OpenTelemetryCacheTelemetry>();
        //
        // and configure your OpenTelemetry Meter/Tracer providers to consume
        // the "Caching.NET.Cache" meter and activity source.

        // Health checks: monitor both the Caching.NET pipeline and Redis connectivity.
        builder.Services.AddHealthChecks()
            .AddCachingHealthChecks(name: "caching-net");

        builder.Services.AddControllers();

        var app = builder.Build();

        // Optional: validate cache registration at startup (fails fast on DI misconfiguration)
        app.Services.ValidateCacheRegistration();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();

        // Expose health endpoint for Kubernetes or other orchestrators.
        app.MapHealthChecks("/health");

        app.MapControllers();

        app.Run();
    }
}
