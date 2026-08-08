using Caching.NET.Internal;
using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests.Internal;

/// <summary>
/// <see cref="CacheGuardPolicy.Warn"/> lets the operation through, so the log entry is the only
/// signal an operator gets. These tests exist because the guard previously recorded the metric and
/// dropped the log, leaving a silent limit breach.
/// </summary>
public class GuardLoggingTests
{
    private static (CacheGuard Guard, RecordingLogger Logger) Build(Action<CachingOptions> configure)
    {
        var options = new CachingOptions { CacheName = "default", ApplicationPrefix = "app" };
        configure(options);
        var logger = new RecordingLogger();
        return (new CacheGuard(options, new CacheTelemetryContext(options), logger), logger);
    }

    [Fact]
    public void WarnPolicy_LogsAWarningWithAFingerprintInsteadOfTheRawKey()
    {
        var (guard, logger) = Build(o =>
        {
            o.Security.MaximumKeyLength = 20;
            o.Security.KeyLengthPolicy = CacheGuardPolicy.Warn;
        });

        var key = new string('k', 100);
        guard.ValidateKey(key);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(3010, entry.EventId);
        Assert.DoesNotContain(key, entry.Message, StringComparison.Ordinal);
        Assert.Contains(KeyFingerprint.Compute(key), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WarnPolicy_LogsTheRawKeyOnlyWhenExplicitlyAllowed()
    {
        var (guard, logger) = Build(o =>
        {
            o.Security.MaximumKeyLength = 20;
            o.Security.KeyLengthPolicy = CacheGuardPolicy.Warn;
            o.Security.AllowRawKeysInLogs = true;
        });

        var key = new string('k', 100);
        guard.ValidateKey(key);

        Assert.Contains(key, Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowPolicy_DoesNotAlsoLog()
    {
        var (guard, logger) = Build(o => o.Security.MaximumKeyLength = 20);

        Assert.Throws<ArgumentException>(() => guard.ValidateKey(new string('k', 100)));

        // The exception is the report; logging it as well would report the same defect twice.
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void IgnorePolicy_NeitherLogsNorThrows()
    {
        var (guard, logger) = Build(o =>
        {
            o.Security.MaximumKeyLength = 20;
            o.Security.KeyLengthPolicy = CacheGuardPolicy.Ignore;
        });

        guard.ValidateKey(new string('k', 100));

        Assert.Empty(logger.Entries);
    }

    internal sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, int EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
    }
}
