using System.Globalization;
using Caching.NET;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests.Pod;

/// <summary>
/// One cache "pod" in its own operating-system process, driven line by line over stdin.
/// </summary>
/// <remarks>
/// Two service providers in one process share a CLR, a thread pool and a memory space, so a
/// multi-pod assertion made that way can only ever be an approximation. This process exists so the
/// backplane can be tested the way it actually runs: separate processes, separate L1, one Redis.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 3)
        {
            await Console.Error.WriteLineAsync("usage: <hybrid|redis> <applicationPrefix> <redisConnectionString>");
            return 2;
        }

        var mode = args[0];
        var prefix = args[1];
        var connectionString = args[2];

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddCaching(cache =>
        {
            if (string.Equals(mode, "redis", StringComparison.OrdinalIgnoreCase))
            {
                cache.UseRedis(connectionString);
            }
            else
            {
                cache.UseHybrid(connectionString);
            }

            cache.WithApplicationPrefix(prefix)
                .WithJitter(TimeSpan.Zero)
                .WithDefaultExpiration(TimeSpan.FromMinutes(5))
                // Writes are awaited so the driving test can sequence pods deterministically;
                // backplane delivery stays asynchronous, exactly as in production.
                .WithResilience(r => r.AllowBackgroundDistributedOperations = false);
        });

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        // Signals to the driver that the cache is constructed and the backplane subscribed.
        await WriteAsync("ready");

        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            try
            {
                var response = await ExecuteAsync(cache, parts);
                if (response is null)
                {
                    return 0;
                }

                await WriteAsync(response);
            }
            catch (Exception ex)
            {
                await WriteAsync($"error {ex.GetType().Name}");
            }
        }

        return 0;
    }

    // Returns null to signal shutdown.
    private static async Task<string?> ExecuteAsync(ICacheService cache, string[] parts) => parts[0] switch
    {
        "set" => await SetAsync(cache, parts[1], parts[2]),
        "set-nobackplane" => await SetNoBackplaneAsync(cache, parts[1], parts[2]),
        "get" => await GetAsync(cache, parts[1]),
        "poll" => await PollAsync(cache, parts[1], parts[2], int.Parse(parts[3], CultureInfo.InvariantCulture)),
        "pollmissing" => await PollMissingAsync(cache, parts[1], int.Parse(parts[2], CultureInfo.InvariantCulture)),
        "settagged" => await SetTaggedAsync(cache, parts[1], parts[2], parts[3]),
        "remove" => await RemoveAsync(cache, parts[1]),
        "removebytag" => await RemoveByTagAsync(cache, parts[1]),
        "clear" => await ClearAsync(cache),
        "exit" => null,
        _ => $"error unknown-command:{parts[0]}"
    };

    private static async Task<string> SetAsync(ICacheService cache, string key, string value)
    {
        await cache.SetAsync(key, value);
        return "ok";
    }

    // Mirrors SetAsync but suppresses the backplane invalidation this process would otherwise publish
    // to every other pod, so a bulk warm-up can write without evicting every other instance's L1.
    private static async Task<string> SetNoBackplaneAsync(ICacheService cache, string key, string value)
    {
        await cache.SetAsync(key, value, new CacheEntryOverrides { SkipBackplaneNotification = true });
        return "ok";
    }

    private static async Task<string> SetTaggedAsync(ICacheService cache, string key, string value, string tag)
    {
        await cache.SetAsync(key, value, tags: [tag]);
        return "ok";
    }

    private static async Task<string> GetAsync(ICacheService cache, string key)
    {
        var result = await cache.TryGetAsync<string>(key);
        return result.HasValue ? result.Value : "<null>";
    }

    private static async Task<string> PollAsync(ICacheService cache, string key, string expected, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        do
        {
            var result = await cache.TryGetAsync<string>(key);
            if (result.HasValue && string.Equals(result.Value, expected, StringComparison.Ordinal))
            {
                return "ok";
            }

            await Task.Delay(25);
        }
        while (Environment.TickCount64 < deadline);

        var final = await cache.TryGetAsync<string>(key);
        return $"timeout last={(final.HasValue ? final.Value : "<null>")}";
    }

    private static async Task<string> PollMissingAsync(ICacheService cache, string key, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        do
        {
            if (!(await cache.TryGetAsync<string>(key)).HasValue)
            {
                return "ok";
            }

            await Task.Delay(25);
        }
        while (Environment.TickCount64 < deadline);

        return "timeout still-present";
    }

    private static async Task<string> RemoveAsync(ICacheService cache, string key)
    {
        await cache.RemoveAsync(key);
        return "ok";
    }

    private static async Task<string> RemoveByTagAsync(ICacheService cache, string tag)
    {
        await cache.RemoveByTagAsync(tag);
        return "ok";
    }

    private static async Task<string> ClearAsync(ICacheService cache)
    {
        await cache.ClearAsync();
        return "ok";
    }

    private static async Task WriteAsync(string response)
    {
        await Console.Out.WriteLineAsync(response);
        await Console.Out.FlushAsync();
    }
}
