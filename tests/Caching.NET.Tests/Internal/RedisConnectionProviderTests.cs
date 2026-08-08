using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caching.NET.Tests.Internal;

/// <summary>
/// The connection provider's "do not memoize the failure" contract, on the path where it used to
/// break.
/// </summary>
public class RedisConnectionProviderTests
{
    private static RedisConnectionProvider Build(string configuration)
    {
        var options = new CachingOptions
        {
            CacheName = "default",
            ApplicationPrefix = "app",
            Mode = CacheMode.Redis,
            Redis = { Configuration = configuration }
        };

        return new RedisConnectionProvider(
            options.CacheName,
            options.Redis,
            NullLogger.Instance,
            new CacheTelemetryContext(options));
    }

    /// <summary>
    /// <c>ConnectAsync</c> is an <c>async</c> method, so a failure raised <b>before</b> its first
    /// <c>await</c> — here <c>ConfigurationOptions.Parse</c> rejecting a malformed connection string —
    /// runs the <c>catch</c> that clears <c>_connectionTask</c> while the caller is still inside the
    /// call, before there is anything to clear, and then returns an already-faulted task. The old
    /// <c>_connectionTask ??= ConnectAsync()</c> memoized that faulted task, undoing the clear and
    /// poisoning the cache permanently: every later operation replayed the same stale failure instead
    /// of retrying. Observed by task identity — a provider that retries hands out a new task.
    /// </summary>
    [Fact]
    public async Task AConnectThatFailsBeforeItsFirstAwait_IsNotMemoized()
    {
        using var provider = Build("localhost:6379,ssl=notabool");

        var first = provider.GetConnectionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => first);

        var second = provider.GetConnectionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => second);

        Assert.NotSame(first, second);
    }
}
