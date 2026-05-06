using Caching.NET.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caching.NET.Internal;

internal sealed class RedisConnectionRotator : IHostedService, IDisposable
{
    private readonly IOptionsMonitor<CacheOptions> _monitor;
    private readonly Func<string, object> _multiplexerFactory;
    private readonly ILogger<RedisConnectionRotator> _logger;
    private IDisposable? _subscription;
    private object? _current;
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
            _current = _multiplexerFactory(initial.RedisConnectionString);
            _currentConnString = initial.RedisConnectionString;
        }
        _subscription = _monitor.OnChange(HandleChange);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        TryDispose(_current);
        _current = null;
        return Task.CompletedTask;
    }

    private void HandleChange(CacheOptions next, string? _)
    {
        if (next.Mode is not (CacheMode.Redis or CacheMode.Hybrid)) return;
        if (string.IsNullOrEmpty(next.RedisConnectionString)) return;
        if (string.Equals(_currentConnString, next.RedisConnectionString, StringComparison.Ordinal)) return;

        _logger.LogInformation("Redis connection string changed; rotating multiplexer.");
        var oldMux = _current;
        _current = _multiplexerFactory(next.RedisConnectionString);
        _currentConnString = next.RedisConnectionString;
        TryDispose(oldMux);
    }

    private static void TryDispose(object? obj)
    {
        if (obj is IDisposable d) d.Dispose();
        else if (obj is IAsyncDisposable ad) _ = ad.DisposeAsync().AsTask();
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();
}
