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
/// <remarks>
/// <para>
/// The adapter is also where the engine's per-operation log <i>volume</i> is brought under
/// Caching.NET's control. The engine writes one line per cache call, plus one per result, at
/// <see cref="LogLevel.Information"/> — the level production runs at — so an application doing
/// ordinary cache reads emits a couple of log lines per read describing cache hits. The engine
/// exposes level knobs for its error categories but none for these, so the rewrite happens here:
/// lines at exactly <see cref="LogLevel.Information"/> are written at
/// <see cref="Options.CacheObservabilityOptions.EngineOperationLogLevel"/> instead (default
/// <see cref="LogLevel.Debug"/>). Warnings and errors are never touched.
/// </para>
/// </remarks>
/// <typeparam name="TCategoryName">The type the consuming component expects a logger for.</typeparam>
internal sealed class CachingCategoryLogger<TCategoryName> : ILogger<TCategoryName>
{
    /// <summary>Root logging category for Caching.NET.</summary>
    public const string RootCategory = "Caching.NET";

    private readonly ILogger _inner;
    private readonly bool _redactKeys;
    private readonly LogLevel _operationLogLevel;

    public CachingCategoryLogger(
        ILoggerFactory loggerFactory,
        string category = RootCategory,
        bool redactKeys = true,
        LogLevel operationLogLevel = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _inner = loggerFactory.CreateLogger(category);
        _redactKeys = redactKeys;
        _operationLogLevel = operationLogLevel;
    }

    /// <summary>Whether the engine's <see cref="LogLevel.Information"/> lines are being rewritten.</summary>
    private bool RewritesOperationLines => _operationLogLevel != LogLevel.Information;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    /// <summary>
    /// Answers for <see cref="LogLevel.Information"/> using the rewritten level.
    /// </summary>
    /// <remarks>
    /// This is what makes the rewrite worth doing rather than merely tidy. The engine calls
    /// <see cref="ILogger.IsEnabled"/> before building its per-operation message, and that message
    /// renders the entry's whole resolved options set into a string. Answering the engine's
    /// <see cref="LogLevel.Information"/> question with the rewritten level means a suppressed line
    /// is never formatted at all, instead of being formatted and then dropped. The engine reports
    /// nothing but per-operation lines at <see cref="LogLevel.Information"/> — every other category
    /// it logs has an explicit level that <see cref="CacheEngineFactory"/> sets — so answering for
    /// the whole level rather than for individual lines does not hide anything else.
    /// </remarks>
    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel != LogLevel.Information || !RewritesOperationLines)
        {
            return _inner.IsEnabled(logLevel);
        }

        return _operationLogLevel != LogLevel.None && _inner.IsEnabled(_operationLogLevel);
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var effectiveLevel = logLevel;

        if (logLevel == LogLevel.Information && RewritesOperationLines)
        {
            if (_operationLogLevel == LogLevel.None || !_inner.IsEnabled(_operationLogLevel))
            {
                return;
            }

            effectiveLevel = _operationLogLevel;
        }

        // TryCreate returns null for every line that carries no cache key, which is the common case
        // and the whole cost of the check: one type test and a short scan of the property list.
        if (_redactKeys && RedactedLogValues.TryCreate(state) is { } redacted)
        {
            _inner.Log(effectiveLevel, eventId, redacted, exception, static (s, _) => s.ToString());
            return;
        }

        _inner.Log(effectiveLevel, eventId, state, exception, formatter);
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
