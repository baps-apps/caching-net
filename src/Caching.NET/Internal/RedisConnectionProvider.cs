using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Caching.NET.Internal;

/// <summary>
/// Owns the single <see cref="IConnectionMultiplexer"/> used by one Caching.NET cache instance.
/// The same multiplexer backs both the distributed cache layer and the backplane, so a cache never
/// opens more than one Redis connection and never opens one per operation.
/// </summary>
/// <remarks>
/// Registered as a singleton and disposed by the container. Connection creation is deferred until
/// the first cache operation; a failed attempt is not cached, so a Redis outage during startup does
/// not permanently poison the cache.
/// </remarks>
internal sealed class RedisConnectionProvider : IDisposable
{
    private readonly object _gate = new();
    private readonly string _cacheName;
    private readonly RedisOptions _options;
    private readonly ILogger _logger;
    private readonly CacheTelemetryContext _telemetry;
    private readonly RedisCertificateValidator _certificateValidator;

    private IConnectionMultiplexer? _connection;
    private Task<IConnectionMultiplexer>? _connectionTask;
    private bool _disposed;

    public RedisConnectionProvider(
        string cacheName,
        RedisOptions options,
        ILogger logger,
        CacheTelemetryContext telemetry)
    {
        _cacheName = cacheName;
        _options = options;
        _logger = logger;
        _telemetry = telemetry;
        _certificateValidator = new RedisCertificateValidator(cacheName, options.StrictCertificateValidation, logger, telemetry);
    }

    /// <summary>
    /// Returns the shared multiplexer, connecting on first use. Handed to both the distributed
    /// cache adapter and the backplane as a factory delegate.
    /// </summary>
    public Task<IConnectionMultiplexer> GetConnectionAsync()
    {
        var existing = Volatile.Read(ref _connectionTask);
        if (existing is not null)
        {
            return existing;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Started, not awaited, inside the lock: the connect itself must not run under the lock
            // or every caller during an outage would block a thread-pool thread for the full connect
            // timeout, one after another. The first caller starts it; everyone else awaits the same
            // task.
            return _connectionTask ??= ConnectAsync();
        }
    }

    private async Task<IConnectionMultiplexer> ConnectAsync()
    {
        try
        {
            var connection = await ConnectionMultiplexer
                .ConnectAsync(BuildConfiguration())
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_disposed)
                {
                    connection.Dispose();
                    throw new ObjectDisposedException(nameof(RedisConnectionProvider));
                }

                connection.ConnectionFailed += OnConnectionFailed;
                connection.ConnectionRestored += OnConnectionRestored;
                _connection = connection;
            }

            return connection;
        }
        catch (Exception ex)
        {
            // Do not memoize the failure: the next operation retries, so a Redis outage during
            // startup degrades the cache instead of permanently disabling it.
            lock (_gate)
            {
                if (!_disposed)
                {
                    _connectionTask = null;
                }
            }

            _telemetry.RecordError(CacheLayers.Redis, ex.GetType().Name);
            CacheLogMessages.RedisConnectionUnavailable(
                _logger,
                ex,
                _cacheName,
                RedisConnectionStringRedactor.Redact(_options.Configuration));
            throw;
        }
    }

    internal ConfigurationOptions BuildConfiguration()
    {
        var configuration = string.IsNullOrWhiteSpace(_options.Configuration)
            ? new ConfigurationOptions()
            : ConfigurationOptions.Parse(_options.Configuration, ignoreUnknown: true);

        configuration.AbortOnConnectFail = _options.AbortOnConnectFail;
        configuration.ConnectTimeout = (int)_options.ConnectTimeout.TotalMilliseconds;
        configuration.SyncTimeout = (int)_options.CommandTimeout.TotalMilliseconds;
        configuration.AsyncTimeout = (int)_options.CommandTimeout.TotalMilliseconds;
        configuration.ConnectRetry = _options.ConnectRetry;
        configuration.ClientName = _options.ClientName ?? $"caching-net:{_cacheName}";

        if (_options.Database is { } database)
        {
            configuration.DefaultDatabase = database;
        }

        if (_options.UseTls)
        {
            configuration.Ssl = true;
        }

        if (_options.KeepAliveSeconds is { } keepAlive)
        {
            configuration.KeepAlive = keepAlive;
        }

        if (configuration.Ssl)
        {
            configuration.CertificateValidation += _certificateValidator.Validate;
        }

        // Applied last so code-first configuration always wins over bound settings.
        _options.ConfigureConnection?.Invoke(configuration);
        return configuration;
    }

    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs e)
    {
        _telemetry.RecordError(CacheLayers.Redis, e.FailureType.ToString());
        CacheLogMessages.RedisConnectionFailed(_logger, _cacheName, DescribeEndpoint(e.EndPoint), e.FailureType.ToString());
    }

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs e)
        => CacheLogMessages.RedisConnectionRestored(_logger, _cacheName, DescribeEndpoint(e.EndPoint));

    // Only the host is logged: a Redis connection string can carry a password, and the full
    // endpoint is not needed to diagnose a connectivity problem.
    private static string DescribeEndpoint(System.Net.EndPoint? endPoint) => endPoint switch
    {
        System.Net.DnsEndPoint dns => dns.Host,
        System.Net.IPEndPoint ip => ip.Address.ToString(),
        null => "unknown",
        _ => "unknown"
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_connection is not null)
            {
                _connection.ConnectionFailed -= OnConnectionFailed;
                _connection.ConnectionRestored -= OnConnectionRestored;
                _connection.Dispose();
                _connection = null;
            }

            _connectionTask = null;
        }
    }
}
