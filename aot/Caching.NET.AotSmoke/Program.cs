using System.Text.Json;
using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.AotSmoke;

/// <summary>
/// Native-AOT smoke test: registers Caching.NET, exercises the cache API, and exits non-zero on
/// any unexpected result. In-memory mode only, so the binary needs no external dependency.
/// </summary>
public static class Program
{
    /// <summary>Runs the smoke test.</summary>
    /// <returns>0 on success, 1 on failure.</returns>
    public static async Task<int> Main()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddCaching(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("aot-smoke")
            .WithDefaultExpiration(TimeSpan.FromMinutes(5))
            .WithJsonSerialization(new JsonSerializerOptions
            {
                TypeInfoResolver = AppJsonContext.Default
            }));

        await using var provider = services.BuildServiceProvider();
        provider.ValidateCachingRegistration();

        var cache = provider.GetRequiredService<IFusionCache>();
        var caches = provider.GetRequiredService<ICacheProvider>();
        var guard = provider.GetRequiredService<ICacheGuard>();

        var factoryCalls = 0;
        var product = await cache.GetOrSetAsync<Product>("Product:1", async _ =>
        {
            factoryCalls++;
            return new Product(1, "widget");
        });

        var cached = await cache.GetOrDefaultAsync<Product>("Product:1");

        await cache.SetAsync("Product:2", new Product(2, "gadget"), tags: ["category:tools"]);
        await cache.RemoveByTagAsync("category:tools");
        var afterTagRemoval = await cache.TryGetAsync<Product>("Product:2");

        await cache.RemoveAsync("Product:1");
        var afterRemoval = await cache.TryGetAsync<Product>("Product:1");

        var checks = new (string Name, bool Ok)[]
        {
            ("factory ran once", factoryCalls == 1),
            ("value returned", product == new Product(1, "widget")),
            ("value cached", cached == new Product(1, "widget")),
            ("tag removal", !afterTagRemoval.HasValue),
            ("key removal", !afterRemoval.HasValue),
            ("provider resolves default", ReferenceEquals(caches.Default, cache)),
            ("provider lists the cache", caches.CacheNames.Count == 1),
            ("guard fingerprints keys", guard.Fingerprint("Product:1").Length == 16)
        };

        var failed = 0;
        foreach (var (name, ok) in checks)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
            if (!ok)
            {
                failed++;
            }
        }

        Console.WriteLine(failed == 0
            ? "Caching.NET AOT smoke test passed."
            : $"Caching.NET AOT smoke test failed: {failed} check(s).");

        return failed == 0 ? 0 : 1;
    }
}
