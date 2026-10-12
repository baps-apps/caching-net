using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests.Registration;

/// <summary>
/// The startup warning for Hybrid mode with no backplane.
/// </summary>
/// <remarks>
/// The topology is allowed — a single-replica deployment has nothing to invalidate — so it is not a
/// validation failure. It is warned about because the failure mode only appears once a second
/// replica exists: each instance keeps serving its own L1 copy of a value another instance has
/// already changed. <c>UseHybrid(...)</c> and a Hybrid cache bound from configuration both turn the
/// backplane on unless it is set, so the warning fires only for a backplane turned off explicitly —
/// or a Hybrid mode set on <c>CachingOptions</c> directly in code, which has no default to apply.
/// </remarks>
public class HybridBackplaneWarningTests
{
    private const int WarningEventId = 3051;
    private const string Redis = "127.0.0.1:1,abortConnect=false,connectTimeout=250";

    [Fact]
    public void HybridWithoutBackplane_WarnsAtStartup()
    {
        var entries = Build(cache => cache
            .UseHybrid(Redis, enableBackplane: false)
            .WithApplicationPrefix("bp-off"));

        var warning = Assert.Single(entries, e => e.EventId == WarningEventId);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("Backplane.Enabled", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HybridBoundFromConfiguration_GetsTheBackplaneAndDoesNotWarn()
    {
        var entries = BuildFromConfiguration(new Dictionary<string, string?>
        {
            ["CacheOptions:Mode"] = "Hybrid",
            ["CacheOptions:ApplicationPrefix"] = "bp-config",
            ["CacheOptions:Redis:Configuration"] = Redis
        });

        Assert.DoesNotContain(entries, e => e.EventId == WarningEventId);
    }

    [Fact]
    public void HybridBoundFromConfiguration_WithTheBackplaneTurnedOff_Warns()
    {
        var entries = BuildFromConfiguration(new Dictionary<string, string?>
        {
            ["CacheOptions:Mode"] = "Hybrid",
            ["CacheOptions:ApplicationPrefix"] = "bp-config-off",
            ["CacheOptions:Redis:Configuration"] = Redis,
            ["CacheOptions:Backplane:Enabled"] = "false"
        });

        Assert.Contains(entries, e => e.EventId == WarningEventId);
    }

    [Fact]
    public void HybridWithBackplane_DoesNotWarn()
    {
        var entries = Build(cache => cache
            .UseHybrid(Redis)
            .WithApplicationPrefix("bp-on"));

        Assert.DoesNotContain(entries, e => e.EventId == WarningEventId);
    }

    [Theory]
    [InlineData(CacheMode.InMemory)]
    [InlineData(CacheMode.Redis)]
    public void OtherModes_DoNotWarn(CacheMode mode)
    {
        // Neither mode keeps a local copy that another instance could invalidate: InMemory has no
        // peers to hear from, and Redis mode holds nothing locally. Validation already rejects a
        // backplane in Redis mode outright.
        var entries = Build(cache =>
        {
            if (mode == CacheMode.InMemory)
            {
                cache.UseInMemory();
            }
            else
            {
                cache.UseRedis(Redis);
            }

            cache.WithApplicationPrefix("bp-other");
        });

        Assert.DoesNotContain(entries, e => e.EventId == WarningEventId);
    }

    [Fact]
    public void TheWarningReportsTheWindowTheOperatorIsAcceptingg()
    {
        var entries = Build(cache => cache
            .UseHybrid(Redis, enableBackplane: false)
            .WithApplicationPrefix("bp-window")
            .WithDefaultExpiration(TimeSpan.FromMinutes(10))
            .WithLocalExpiration(TimeSpan.FromSeconds(30))
            .WithDistributedExpiration(TimeSpan.FromMinutes(10)));

        var warning = Assert.Single(entries, e => e.EventId == WarningEventId);

        // The local lifetime, not the default one, is what bounds the stale window.
        Assert.Contains("00:00:30", warning.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<(LogLevel Level, int EventId, string Message)> BuildFromConfiguration(
        Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var recorder = new RecordingProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(recorder));
        services.AddCaching(configuration);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ICacheService>();

        return recorder.Entries;
    }

    private static IReadOnlyList<(LogLevel Level, int EventId, string Message)> Build(Action<CachingBuilder> configure)
    {
        var recorder = new RecordingProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(recorder));
        services.AddCaching(configure);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ICacheService>();

        return recorder.Entries;
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
