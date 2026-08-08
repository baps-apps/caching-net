using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests.Internal;

/// <summary>
/// The internal cache engine logs every cache call, and every cache result, at
/// <see cref="LogLevel.Information"/> — the level a production application normally runs at. Left
/// alone that is a couple of log lines per cache read describing cache hits, which is a logging bill
/// rather than a diagnostic. Caching.NET rewrites those lines to
/// <see cref="CacheObservabilityOptions.EngineOperationLogLevel"/>.
/// </summary>
public class EngineOperationLogLevelTests
{
    private const int Operations = 50;

    private static async Task<CountingLoggerProvider> RunOperationsAsync(
        LogLevel minimumLevel,
        Action<CachingOptions>? configure = null)
    {
        var sink = new CountingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            builder.AddProvider(sink);
        });
        services.AddCachingOptions(options =>
        {
            options.Mode = CacheMode.InMemory;
            options.ApplicationPrefix = "tests";
            options.Observability.LogStartupSummary = false;
            configure?.Invoke(options);
        });

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        sink.Reset();
        for (var i = 0; i < Operations; i++)
        {
            await cache.GetOrSetAsync<int>($"Order:{i % 5}", _ => Task.FromResult(i));
        }

        return sink;
    }

    [Fact]
    public async Task AtInformation_TheEngineEmitsNoPerOperationLines()
    {
        var sink = await RunOperationsAsync(LogLevel.Information);

        Assert.Equal(0, sink.Total);
    }

    [Fact]
    public async Task AtDebug_ThePerOperationLinesAreStillAvailable()
    {
        var sink = await RunOperationsAsync(LogLevel.Debug);

        Assert.True(sink.Total > 0, "the per-operation lines should reappear at Debug");
        Assert.Equal(0, sink.CountAt(LogLevel.Information));
    }

    [Fact]
    public async Task SettingTheLevelBackToInformation_RestoresEngineVerbosity()
    {
        var sink = await RunOperationsAsync(
            LogLevel.Information,
            options => options.Observability.EngineOperationLogLevel = LogLevel.Information);

        Assert.True(
            sink.CountAt(LogLevel.Information) >= Operations,
            $"expected at least one Information line per operation, got {sink.CountAt(LogLevel.Information)}");
    }

    [Fact]
    public async Task SettingTheLevelToNone_DropsThePerOperationLinesEntirely()
    {
        var sink = await RunOperationsAsync(
            LogLevel.Trace,
            options => options.Observability.EngineOperationLogLevel = LogLevel.None);

        Assert.Equal(0, sink.CountAt(LogLevel.Information));
    }

    /// <summary>
    /// The rewrite must never touch a warning or an error: those are the lines an operator is
    /// relying on during an incident.
    /// </summary>
    [Fact]
    public async Task WarningsAndAboveAreNeverRewritten()
    {
        var sink = new CountingLoggerProvider();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(sink);
        });

        var logger = new global::Caching.NET.Internal.CachingCategoryLogger<EngineOperationLogLevelTests>(
            factory,
            operationLogLevel: LogLevel.None);

        logger.Log(LogLevel.Warning, default, "state", null, static (s, _) => s);
        logger.Log(LogLevel.Error, default, "state", null, static (s, _) => s);
        logger.Log(LogLevel.Critical, default, "state", null, static (s, _) => s);
        logger.Log(LogLevel.Information, default, "state", null, static (s, _) => s);

        Assert.Equal(1, sink.CountAt(LogLevel.Warning));
        Assert.Equal(1, sink.CountAt(LogLevel.Error));
        Assert.Equal(1, sink.CountAt(LogLevel.Critical));
        Assert.Equal(0, sink.CountAt(LogLevel.Information));
    }

    /// <summary>
    /// The engine asks <c>IsEnabled</c> before rendering its per-operation message, and that message
    /// renders the entry's whole resolved options set. Answering the engine's Information question
    /// with the rewritten level is what stops a suppressed line from being formatted at all.
    /// </summary>
    [Fact]
    public void IsEnabled_AnswersForInformationUsingTheRewrittenLevel()
    {
        // A provider has to be registered: a LoggerFactory with none reports every level disabled,
        // which would make the assertions below pass for the wrong reason.
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new CountingLoggerProvider());
        });

        var rewritten = new global::Caching.NET.Internal.CachingCategoryLogger<EngineOperationLogLevelTests>(
            factory,
            operationLogLevel: LogLevel.Debug);
        var native = new global::Caching.NET.Internal.CachingCategoryLogger<EngineOperationLogLevelTests>(
            factory,
            operationLogLevel: LogLevel.Information);

        Assert.False(rewritten.IsEnabled(LogLevel.Information));
        Assert.True(rewritten.IsEnabled(LogLevel.Warning));
        Assert.True(native.IsEnabled(LogLevel.Information));
    }

    private sealed class CountingLoggerProvider : ILoggerProvider
    {
        private readonly Dictionary<LogLevel, int> _counts = [];

        public int Total { get; private set; }

        public int CountAt(LogLevel level)
        {
            lock (_counts)
            {
                return _counts.GetValueOrDefault(level);
            }
        }

        public void Reset()
        {
            lock (_counts)
            {
                _counts.Clear();
                Total = 0;
            }
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose()
        {
        }

        private sealed class Sink(CountingLoggerProvider owner) : ILogger
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
                lock (owner._counts)
                {
                    owner._counts[logLevel] = owner._counts.GetValueOrDefault(logLevel) + 1;
                    owner.Total++;
                }
            }
        }
    }
}
