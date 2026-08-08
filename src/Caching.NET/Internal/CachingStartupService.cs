using Microsoft.Extensions.Hosting;

namespace Caching.NET.Internal;

/// <summary>
/// Forces every registered cache to be constructed at host start rather than on the first request.
/// </summary>
/// <remarks>
/// Building the cache eagerly is what makes the startup summary appear at boot, surfaces a bad
/// serializer or memory-limit configuration before traffic arrives, and lets the Redis client begin
/// connecting in the background. It deliberately does not block on the Redis connection: with
/// <c>AbortOnConnectFail</c> off, a pod must still become ready while Redis is starting.
/// </remarks>
internal sealed class CachingStartupService : IHostedService
{
    private readonly IEnumerable<CacheRegistration> _registrations;

    public CachingStartupService(IEnumerable<CacheRegistration> registrations)
    {
        _registrations = registrations;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in _registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = registration.Instance;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
