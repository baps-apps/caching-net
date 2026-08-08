using Caching.NET.Extensions;
using Caching.NET.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Internal;

/// <summary>
/// The cache engine writes the physical cache key into a structured <c>CacheKey</c> property on its
/// per-operation log lines, at <c>Information</c> — the level the documentation recommends for the
/// <c>Caching.NET</c> category, and the default level of an ASP.NET Core application. A cache key
/// routinely embeds a user id, an email address or a tenant id, so those lines would put personal
/// data in the application's log sink while <c>Security.AllowRawKeysInLogs</c> claims otherwise.
/// </summary>
public class EngineKeyRedactionTests
{
    private const string SensitiveKey = "User:alice@example.com:profile";

    [Fact]
    public async Task EngineOperationLogs_ReplaceTheRawKeyWithAFingerprint()
    {
        var sink = new CapturingLoggerProvider();

        using (var provider = Build(sink, allowRawKeys: false))
        {
            var cache = provider.GetRequiredService<IFusionCache>();
            await cache.SetAsync(SensitiveKey, 1);
            _ = await cache.GetOrDefaultAsync<int>(SensitiveKey);
        }

        Assert.DoesNotContain(sink.Entries, e => e.Message.Contains("alice@example.com", StringComparison.Ordinal));
        Assert.DoesNotContain(
            sink.Entries.SelectMany(e => e.Values),
            v => v.Value?.ToString()?.Contains("alice@example.com", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TheFingerprintIsTheOneCallersCanComputeFromICacheGuard()
    {
        var sink = new CapturingLoggerProvider();

        using var provider = Build(sink, allowRawKeys: false);
        var cache = provider.GetRequiredService<IFusionCache>();
        var guard = provider.GetRequiredService<ICacheGuard>();

        await cache.SetAsync(SensitiveKey, 1);

        // The engine logs the physical key, so the fingerprint an operator can reproduce from a
        // support ticket is the one over the prefixed key.
        var expected = guard.Fingerprint($"redaction:{SensitiveKey}");

        Assert.Contains(
            sink.Entries.SelectMany(e => e.Values),
            v => string.Equals(v.Key, RedactedLogValues.CacheKeyProperty, StringComparison.Ordinal)
                && string.Equals(v.Value?.ToString(), expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllowRawKeysInLogs_OptsBackIntoTheRawKey()
    {
        var sink = new CapturingLoggerProvider();

        using var provider = Build(sink, allowRawKeys: true);
        await provider.GetRequiredService<IFusionCache>().SetAsync(SensitiveKey, 1);

        Assert.Contains(sink.Entries, e => e.Message.Contains("alice@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RedactionKeepsTheRestOfTheMessageIntact()
    {
        var sink = new CapturingLoggerProvider();

        using var provider = Build(sink, allowRawKeys: false);
        await provider.GetRequiredService<IFusionCache>().SetAsync(SensitiveKey, 1);

        var line = Assert.Single(sink.Entries, e => e.Message.Contains("SetAsync", StringComparison.Ordinal));

        // The template around the hole is preserved: only the key value changes.
        Assert.Contains("FUSION", line.Message, StringComparison.Ordinal);
        Assert.Contains("N=default", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{CacheKey}", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactedLogValues_IgnoresStatesWithoutACacheKey()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Something", "else"),
            new("{OriginalFormat}", "no key here")
        };

        Assert.Null(RedactedLogValues.TryCreate(state));
    }

    [Fact]
    public void RedactedLogValues_RendersEscapedBracesAndUnknownHoles()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new(RedactedLogValues.CacheKeyProperty, "k"),
            new("Known", "yes"),
            new("{OriginalFormat}", "{{literal}} {Known} {Missing} {CacheKey}")
        };

        var redacted = Assert.IsType<RedactedLogValues>(RedactedLogValues.TryCreate(state));

        Assert.Equal($"{{literal}} yes {{Missing}} {KeyFingerprintOf("k")}", redacted.ToString());
    }

    private static string KeyFingerprintOf(string key)
    {
        using var provider = Build(new CapturingLoggerProvider(), allowRawKeys: false);
        return provider.GetRequiredService<ICacheGuard>().Fingerprint(key);
    }

    private static ServiceProvider Build(ILoggerProvider sink, bool allowRawKeys)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(sink);
        });
        services.AddCaching(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("redaction")
            .WithSecurity(s => s.AllowRawKeysInLogs = allowRawKeys));

        return services.BuildServiceProvider();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_entries) { return _entries.ToArray(); } }
        }

        public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

        public void Dispose()
        {
        }

        private void Add(Entry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        internal sealed record Entry(string Category, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values);

        private sealed class Capturing(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
                owner.Add(new Entry(category, formatter(state, exception), values.ToArray()));
            }
        }
    }
}
