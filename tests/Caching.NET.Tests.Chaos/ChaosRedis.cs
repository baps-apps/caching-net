using System.Net;
using System.Net.Sockets;
using Testcontainers.Redis;

namespace Caching.NET.Tests.Chaos;

/// <summary>
/// Starts the Redis container these tests restart mid-run.
/// </summary>
/// <remarks>
/// Docker re-randomises published ports across stop/start, so a chaos test that restarts its
/// container must bind a <em>fixed</em> host port rather than let Docker choose one. Finding a free
/// port means opening a probe listener on port 0 and closing it again to learn the number — and the
/// container binds it some milliseconds later, so the port is unowned in between.
/// <para>
/// Two test classes in this assembly each need a port, and xUnit runs test classes in parallel. Two
/// probes that run back to back are handed the <em>same</em> ephemeral port, because the first
/// listener is already closed when the second one asks. That is not theoretical: it failed
/// <c>RedisOutageTests.InitializeAsync</c> during the v3.0.0 release review, with no leftover
/// container to explain it.
/// </para>
/// <para>
/// Reserving each port for the lifetime of the test process closes the in-process half of the race;
/// retrying on a fresh port covers the rest, where an unrelated process on the machine takes the
/// port first.
/// </para>
/// </remarks>
internal static class ChaosRedis
{
    private const int MaxAttempts = 5;

    private static readonly Lock Gate = new();
    private static readonly HashSet<int> Reserved = [];

    /// <summary>
    /// Starts a Redis container on a fixed, exclusively reserved host port.
    /// </summary>
    /// <returns>The started container and the host port it is bound to.</returns>
    public static async Task<(RedisContainer Container, int HostPort)> StartAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = ReserveFreePort();
            var container = new RedisBuilder("redis:7.4-alpine")
                .WithPortBinding(port, 6379)
                .Build();

            try
            {
                await container.StartAsync();
                return (container, port);
            }
            catch when (attempt < MaxAttempts)
            {
                // The port was taken between the probe and the bind. Keep it reserved — whoever won
                // still holds it — and try a different one.
                await container.DisposeAsync();
            }
        }
    }

    private static int ReserveFreePort()
    {
        lock (Gate)
        {
            for (var attempt = 1; ; attempt++)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();

                if (Reserved.Add(port))
                {
                    return port;
                }

                if (attempt == MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Could not find a host port not already reserved by this test process after {MaxAttempts} attempts.");
                }
            }
        }
    }
}
