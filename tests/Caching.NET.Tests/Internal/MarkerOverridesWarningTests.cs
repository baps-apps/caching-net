using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests.Internal;

/// <summary>
/// The warning for overrides that <c>RemoveByTag</c> and <c>Clear</c> ignore.
/// </summary>
/// <remarks>
/// Both verbs write a marker entry, and only the overrides that change how the marker is written
/// apply to it — lifetime, fail-safe and memory-layer overrides are dropped so they cannot shorten
/// the invalidation. Dropping a setting the caller passed explicitly is correct but should not be
/// silent, so the first such call on a cache logs which properties were ignored. Once per cache: a
/// shared overrides object passed to every invalidation must not log on every call.
/// </remarks>
public class MarkerOverridesWarningTests
{
    private const int WarningEventId = 3041;

    [Fact]
    public async Task RemoveByTag_WithALifetimeOverride_WarnsNamingTheIgnoredProperties()
    {
        var (provider, recorder) = Build();
        using var _ = provider;

        await provider.GetRequiredService<ICacheService>().RemoveByTagAsync(
            "category:tools",
            new CacheEntryOverrides { LocalExpiration = TimeSpan.FromMinutes(5), FailSafe = false });

        var warning = Assert.Single(recorder.Entries, e => e.EventId == WarningEventId);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(nameof(CacheEntryOverrides.LocalExpiration), warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CacheEntryOverrides.FailSafe), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_WithALifetimeOverride_Warns()
    {
        var (provider, recorder) = Build();
        using var _ = provider;

        provider.GetRequiredService<ICacheService>().Clear(
            options: new CacheEntryOverrides { DistributedExpiration = TimeSpan.FromMinutes(5) });

        var warning = Assert.Single(recorder.Entries, e => e.EventId == WarningEventId);
        Assert.Contains(nameof(CacheEntryOverrides.DistributedExpiration), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedInvalidations_WarnOnce()
    {
        var (provider, recorder) = Build();
        using var _ = provider;
        var cache = provider.GetRequiredService<ICacheService>();
        var shared = new CacheEntryOverrides { Priority = CacheEntryPriority.High };

        await cache.RemoveByTagAsync("a", shared);
        await cache.RemoveByTagAsync("b", shared);
        await cache.ClearAsync(options: shared);

        Assert.Single(recorder.Entries, e => e.EventId == WarningEventId);
    }

    [Fact]
    public async Task OperationalOverridesOnly_DoNotWarn()
    {
        var (provider, recorder) = Build();
        using var _ = provider;

        await provider.GetRequiredService<ICacheService>().RemoveByTagAsync(
            "category:tools",
            new CacheEntryOverrides
            {
                AllowBackgroundDistributedOperations = false,
                AllowBackgroundBackplaneOperations = false,
                SkipBackplaneNotification = true,
                DistributedSoftTimeout = TimeSpan.FromSeconds(1),
                DistributedHardTimeout = TimeSpan.FromSeconds(2)
            });

        Assert.DoesNotContain(recorder.Entries, e => e.EventId == WarningEventId);
    }

    [Fact]
    public async Task EntryVerbs_DoNotWarn()
    {
        // The warning is about markers. The same overrides on an ordinary entry all apply.
        var (provider, recorder) = Build();
        using var _ = provider;

        await provider.GetRequiredService<ICacheService>().SetAsync(
            "Product:1", "a", new CacheEntryOverrides { LocalExpiration = TimeSpan.FromMinutes(5) });

        Assert.DoesNotContain(recorder.Entries, e => e.EventId == WarningEventId);
    }

    private static (ServiceProvider Provider, RecordingProvider Recorder) Build()
    {
        var recorder = new RecordingProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(recorder));
        services.AddCaching(cache => cache.UseInMemory().WithApplicationPrefix("marker-warn"));
        return (services.BuildServiceProvider(), recorder);
    }

    private sealed class RecordingProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, int EventId, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, int EventId, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<(LogLevel Level, int EventId, string Message)> sink) : ILogger
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
                lock (sink)
                {
                    sink.Add((logLevel, eventId.Id, formatter(state, exception)));
                }
            }
        }
    }
}
