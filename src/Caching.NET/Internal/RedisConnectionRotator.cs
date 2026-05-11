using Caching.NET.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caching.NET.Internal;

internal sealed class RedisConnectionRotator : IHostedService, IAsyncDisposable
{
    private readonly IOptionsMonitor<CacheOptions> _monitor;
    private readonly Func<string, object> _multiplexerFactory;
    private readonly ILogger<RedisConnectionRotator> _logger;
    private readonly object _gate = new();
    private IDisposable? _subscription;
    private volatile object? _current;
    private string? _currentConnString;

    public RedisConnectionRotator(
        IOptionsMonitor<CacheOptions> monitor,
        Func<string, object> multiplexerFactory,
        ILogger<RedisConnectionRotator> logger)
    {
        _monitor = monitor;
        _multiplexerFactory = multiplexerFactory;
        _logger = logger;
    }

    public object? Current => _current;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var initial = _monitor.CurrentValue;
        if (initial.Mode is CacheMode.Redis or CacheMode.Hybrid && !string.IsNullOrEmpty(initial.RedisConnectionString))
        {
            lock (_gate)
            {
                _current = _multiplexerFactory(initial.RedisConnectionString);
                _currentConnString = initial.RedisConnectionString;
            }
        }
        _subscription = _monitor.OnChange(HandleChange);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        object? old;
        lock (_gate)
        {
            old = _current;
            _current = null;
            _currentConnString = null;
        }
        return DisposeSafelyAsync(old, cancellationToken);
    }

    private void HandleChange(CacheOptions next, string? _name)
    {
        if (next.Mode is not (CacheMode.Redis or CacheMode.Hybrid)) return;
        if (string.IsNullOrEmpty(next.RedisConnectionString)) return;

        lock (_gate)
        {
            if (string.Equals(_currentConnString, next.RedisConnectionString, StringComparison.Ordinal)) return;
            _logger.LogInformation("Redis connection string changed; rotating multiplexer.");
            var oldMux = _current;
            _current = _multiplexerFactory(next.RedisConnectionString);
            _currentConnString = next.RedisConnectionString;
            _ = DisposeSafelyAsync(oldMux, CancellationToken.None);
        }
    }

    private async Task DisposeSafelyAsync(object? obj, CancellationToken cancellationToken)
    {
        if (obj is null) return;
        try
        {
            // Give in-flight operations a brief chance to finish before tearing down old connection.
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Continue disposal on shutdown.
        }

        try
        {
            if (obj is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else if (obj is IDisposable d)
            {
                d.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose rotated Redis multiplexer cleanly.");
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));
}
