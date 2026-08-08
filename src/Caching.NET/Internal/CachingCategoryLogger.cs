using Microsoft.Extensions.Logging;

namespace Caching.NET.Internal;

/// <summary>
/// Presents an <see cref="ILogger{TCategoryName}"/> whose messages are written under a
/// Caching.NET logging category instead of the implementing type's namespace. This is what keeps
/// every log line the package produces filterable as <c>Caching.NET</c>, regardless of which
/// internal component emitted it.
/// </summary>
/// <remarks>
/// The adapter also enforces Caching.NET's key-redaction policy on engine output. The engine logs
/// the physical cache key in a structured <c>CacheKey</c> property on its per-operation lines, and
/// a cache key routinely carries a user id, an email address or a tenant id. Unless
/// <c>Security.AllowRawKeysInLogs</c> is set, the value is replaced with the same fingerprint
/// <see cref="ICacheGuard.Fingerprint"/> produces, so those lines stay correlatable without putting
/// personal data in the application's log sink.
/// </remarks>
/// <typeparam name="TCategoryName">The type the consuming component expects a logger for.</typeparam>
internal sealed class CachingCategoryLogger<TCategoryName> : ILogger<TCategoryName>
{
    /// <summary>Root logging category for Caching.NET.</summary>
    public const string RootCategory = "Caching.NET";

    private readonly ILogger _inner;
    private readonly bool _redactKeys;

    public CachingCategoryLogger(
        ILoggerFactory loggerFactory,
        string category = RootCategory,
        bool redactKeys = true)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _inner = loggerFactory.CreateLogger(category);
        _redactKeys = redactKeys;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // TryCreate returns null for every line that carries no cache key, which is the common case
        // and the whole cost of the check: one type test and a short scan of the property list.
        if (_redactKeys && RedactedLogValues.TryCreate(state) is { } redacted)
        {
            _inner.Log(logLevel, eventId, redacted, exception, static (s, _) => s.ToString());
            return;
        }

        _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

/// <summary>
/// Logging categories used by Caching.NET.
/// </summary>
internal static class CacheLogCategories
{
    public const string Root = "Caching.NET";
    public const string Redis = "Caching.NET.Redis";
    public const string Backplane = "Caching.NET.Backplane";
    public const string Security = "Caching.NET.Security";
    public const string Configuration = "Caching.NET.Configuration";
}
