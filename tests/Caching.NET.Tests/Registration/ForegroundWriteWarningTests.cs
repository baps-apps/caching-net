using Caching.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Registration;

/// <summary>
/// The startup warning for the one option combination whose failure mode contradicts its own
/// setting: <c>AllowBackgroundDistributedOperations: false</c> makes the distributed write run on
/// the caller's path, where the engine propagates serialization failures regardless of
/// <c>ThrowOnSerializationErrors: false</c> — so an entry over <c>MaximumPayloadBytes</c> fails the
/// request rather than going uncached.
/// </summary>
/// <remarks>
/// Caching.NET cannot intercept this without wrapping every cache call, so the mitigation is that
/// the operator is told at boot instead of from a production stack trace. That only helps if the
/// warning actually fires, and only if it stays quiet for every other configuration.
/// </remarks>
public class ForegroundWriteWarningTests
{
    private const int WarningEventId = 3050;

    [Fact]
    public void ForegroundDistributedWrites_WarnAtStartup()
    {
        var entries = Build(cache => cache
            .UseRedis("127.0.0.1:1,abortConnect=false,connectTimeout=250")
            .WithApplicationPrefix("warn-fg")
            .WithResilience(r =>
            {
                r.AllowBackgroundDistributedOperations = false;
                r.ThrowOnSerializationErrors = false;
            }));

        var warning = Assert.Single(entries, e => e.EventId == WarningEventId);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("AllowBackgroundDistributedOperations", warning.Message, StringComparison.Ordinal);
        Assert.Contains("MaximumPayloadBytes", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundDistributedWrites_DoNotWarn()
    {
        var entries = Build(cache => cache
            .UseRedis("127.0.0.1:1,abortConnect=false,connectTimeout=250")
            .WithApplicationPrefix("warn-bg"));

        Assert.DoesNotContain(entries, e => e.EventId == WarningEventId);
    }

    [Fact]
    public void OptingIntoSerializationExceptions_DoesNotWarn()
    {
        // The caller has already said it wants the exception, so there is nothing to warn about.
        var entries = Build(cache => cache
            .UseRedis("127.0.0.1:1,abortConnect=false,connectTimeout=250")
            .WithApplicationPrefix("warn-optin")
            .WithResilience(r =>
            {
                r.AllowBackgroundDistributedOperations = false;
                r.ThrowOnSerializationErrors = true;
            }));

        Assert.DoesNotContain(entries, e => e.EventId == WarningEventId);
    }

    [Fact]
    public void InMemoryMode_DoesNotWarn()
    {
        // No distributed layer means no serializer on any path.
        var entries = Build(cache => cache
            .UseInMemory()
            .WithApplicationPrefix("warn-inmemory")
            .WithResilience(r => r.AllowBackgroundDistributedOperations = false));

        Assert.DoesNotContain(entries, e => e.EventId == WarningEventId);
    }

    private static IReadOnlyList<(LogLevel Level, int EventId, string Message)> Build(Action<CachingBuilder> configure)
    {
        var recorder = new RecordingProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(recorder));
        services.AddCaching(configure);

        using var provider = services.BuildServiceProvider();

        // Resolving the cache is what runs the factory, and therefore the warning. No Redis is
        // needed: the connection is opened lazily on the first operation, which never happens here.
        _ = provider.GetRequiredService<IFusionCache>();

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

        private sealed class RecordingLogger : ILogger
        {
            private readonly List<(LogLevel Level, int EventId, string Message)> _sink;

            public RecordingLogger(List<(LogLevel Level, int EventId, string Message)> sink)
            {
                _sink = sink;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_sink)
                {
                    _sink.Add((logLevel, eventId.Id, formatter(state, exception)));
                }
            }
        }
    }
}
