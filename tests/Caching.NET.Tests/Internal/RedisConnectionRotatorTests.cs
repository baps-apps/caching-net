using Caching.NET.Internal;
using Caching.NET.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caching.NET.Tests.Internal;

public class RedisConnectionRotatorTests
{
    [Fact]
    public async Task When_RedisConnectionString_changes_rotator_invokes_factory_again()
    {
        var built = 0;
        var monitor = new TestMonitor(new CacheOptions
        {
            KeyPrefix = "rotate", Mode = CacheMode.Redis, RedisConnectionString = "host-a:6379"
        });

        Func<string, object> factory = _ => { built++; return new object(); };

        var rotator = new RedisConnectionRotator(monitor, factory, NullLogger<RedisConnectionRotator>.Instance);

        await rotator.StartAsync(CancellationToken.None);
        Assert.Equal(1, built);

        monitor.Trigger(new CacheOptions
        {
            KeyPrefix = "rotate", Mode = CacheMode.Redis, RedisConnectionString = "host-b:6379"
        });

        Assert.Equal(2, built);

        await rotator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task When_connection_string_unchanged_rotator_does_not_rebuild()
    {
        var built = 0;
        var monitor = new TestMonitor(new CacheOptions
        {
            Mode = CacheMode.Redis, RedisConnectionString = "host-a:6379", KeyPrefix = "x"
        });
        Func<string, object> factory = _ => { built++; return new object(); };
        var rotator = new RedisConnectionRotator(monitor, factory, NullLogger<RedisConnectionRotator>.Instance);

        await rotator.StartAsync(CancellationToken.None);
        monitor.Trigger(new CacheOptions { Mode = CacheMode.Redis, RedisConnectionString = "host-a:6379", KeyPrefix = "x" });

        Assert.Equal(1, built); // no rebuild for same connection string

        await rotator.StopAsync(CancellationToken.None);
    }

    private sealed class TestMonitor : IOptionsMonitor<CacheOptions>
    {
        private CacheOptions _current;
        private Action<CacheOptions, string?>? _listener;
        public TestMonitor(CacheOptions initial) => _current = initial;
        public CacheOptions CurrentValue => _current;
        public CacheOptions Get(string? name) => _current;
        public IDisposable? OnChange(Action<CacheOptions, string?> listener)
        {
            _listener = listener;
            return new Empty();
        }
        public void Trigger(CacheOptions next)
        {
            _current = next;
            _listener?.Invoke(_current, null);
        }
        private sealed class Empty : IDisposable { public void Dispose() { } }
    }
}
