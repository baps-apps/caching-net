using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Caching.NET;
using Caching.NET.Extensions;
using Caching.NET.Health;
using Caching.NET.Options;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Integration;

/// <summary>
/// Redis authentication and TLS against real, hardened Redis containers.
/// </summary>
/// <remarks>
/// These paths are the ones that fail closed in production and cannot be exercised against a plain
/// Redis: a password-protected server, and a TLS server whose certificate the client has no reason
/// to trust. Own containers rather than the shared fixture, because the whole point is a server
/// configured differently.
/// </remarks>
public sealed class RedisSecurityTests : IAsyncLifetime
{
    private const string Password = "c0rrect-horse-battery-staple";

    private IContainer _authenticated = null!;
    private IContainer _tls = null!;

    public async Task InitializeAsync()
    {
        _authenticated = new ContainerBuilder("redis:7.4-alpine")
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithCommand("redis-server", "--requirepass", Password)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();

        var (certificatePem, keyPem) = CreateServerCertificate();

        _tls = new ContainerBuilder("redis:7.4-alpine")
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithResourceMapping(certificatePem, "/tls/redis.crt")
            .WithResourceMapping(keyPem, "/tls/redis.key")
            .WithResourceMapping(certificatePem, "/tls/ca.crt")
            .WithCommand(
                "redis-server",
                "--port", "0",
                "--tls-port", "6379",
                "--tls-cert-file", "/tls/redis.crt",
                "--tls-key-file", "/tls/redis.key",
                "--tls-ca-cert-file", "/tls/ca.crt",
                "--tls-auth-clients", "no")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();

        await Task.WhenAll(_authenticated.StartAsync(), _tls.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await _authenticated.DisposeAsync();
        await _tls.DisposeAsync();
    }

    private string AuthenticatedEndpoint => $"127.0.0.1:{_authenticated.GetMappedPublicPort(6379)}";

    private string TlsEndpoint => $"127.0.0.1:{_tls.GetMappedPublicPort(6379)}";

    // A certificate the test process has no reason to trust: it is not in any trust store, and its
    // subject is not the host the client connects to. That is precisely the situation the validator
    // exists to judge.
    private static (byte[] CertificatePem, byte[] KeyPem) CreateServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=caching-net-redis",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        return (
            System.Text.Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()),
            System.Text.Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()));
    }

    private static ServiceProvider Build(string prefix, Action<CachingBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddCaching(cache =>
        {
            cache.WithApplicationPrefix(prefix)
                .WithJitter(TimeSpan.Zero)
                .WithRedis(r =>
                {
                    r.ConnectTimeout = TimeSpan.FromSeconds(3);
                    r.CommandTimeout = TimeSpan.FromSeconds(2);
                })
                .WithResilience(r => r.AllowBackgroundDistributedOperations = false);
            configure(cache);
        });

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private static HealthCheckContext HealthContext() => new()
    {
        Registration = new HealthCheckRegistration("caching-net", _ => null!, HealthStatus.Unhealthy, tags: null)
    };

    [Fact]
    public async Task CorrectPassword_RoundTripsThroughRedis()
    {
        await using var provider = Build("redis-auth-ok", cache => cache
            .UseRedis($"{AuthenticatedEndpoint},password={Password},abortConnect=false"));

        var cache = provider.GetRequiredService<IFusionCache>();
        await cache.SetAsync("Order:1", 1);

        Assert.Equal(1, await cache.GetOrDefaultAsync<int>("Order:1"));
    }

    [Fact]
    public async Task WrongPassword_IsNotHiddenFromTheCaller()
    {
        await using var provider = Build("redis-auth-wrong", cache => cache
            .UseRedis($"{AuthenticatedEndpoint},password=not-the-password,abortConnect=false")
            // An authentication failure is a configuration defect, not a transient outage: an
            // application that asks for distributed errors must be told.
            .WithResilience(r => r.ThrowOnDistributedCacheErrors = true));

        var cache = provider.GetRequiredService<IFusionCache>();

        await Assert.ThrowsAnyAsync<Exception>(async () => await cache.SetAsync("Order:2", 2));
    }

    [Fact]
    public async Task WrongPassword_DegradesRatherThanFailingTheCallerByDefault()
    {
        await using var provider = Build("redis-auth-degraded", cache => cache
            .UseRedis($"{AuthenticatedEndpoint},password=not-the-password,abortConnect=false"));

        var cache = provider.GetRequiredService<IFusionCache>();

        // Default resilience: the request still succeeds from the factory...
        Assert.Equal(5, await cache.GetOrSetAsync<int>("Order:3", async _ => 5));

        // ...but readiness must not report a cache that cannot authenticate as healthy.
        var readiness = new CachingHealthCheck(
            provider.GetRequiredService<ICacheProvider>(),
            provider.GetRequiredService<IOptionsMonitor<CachingOptions>>());

        Assert.Equal(HealthStatus.Degraded, (await readiness.CheckHealthAsync(HealthContext())).Status);
    }

    [Fact]
    public async Task UntrustedTlsCertificate_IsRejectedUnderStrictValidation()
    {
        await using var provider = Build("redis-tls-strict", cache => cache
            .UseRedis($"{TlsEndpoint},abortConnect=false")
            .WithStrictRedisTls()
            .WithResilience(r => r.ThrowOnDistributedCacheErrors = true));

        var cache = provider.GetRequiredService<IFusionCache>();

        await Assert.ThrowsAnyAsync<Exception>(async () => await cache.SetAsync("Order:4", 4));
    }

    [Fact]
    public async Task TlsRejection_IsReportedThroughTheTlsValidationMetric()
    {
        // caching.net.redis.tls.validations is documented and is what an operator alerts on when a
        // certificate expires or a chain breaks. It cannot be produced without a real TLS server, so
        // it is asserted here rather than left as a documented instrument nothing proves.
        using var validations = new TlsValidationCounter();

        await using var provider = Build("redis-tls-metric", cache => cache
            .UseRedis($"{TlsEndpoint},abortConnect=false")
            .WithStrictRedisTls()
            .WithResilience(r => r.ThrowOnDistributedCacheErrors = true));

        var cache = provider.GetRequiredService<IFusionCache>();
        await Assert.ThrowsAnyAsync<Exception>(async () => await cache.SetAsync("Order:5", 5));

        Assert.True(
            await validations.WaitForAnyAsync(),
            "a rejected TLS certificate must be recorded on caching.net.redis.tls.validations");

        // The dimension is a classification, never the certificate subject, issuer or endpoint.
        Assert.All(validations.Results, result => Assert.Contains(
            result,
            new[] { "chain_error", "name_mismatch", "certificate_missing", "untrusted" }));
    }

    /// <summary>
    /// Collects <c>caching.net.redis.tls.validations</c> and keeps the recorded <c>cache.result</c>
    /// values so the test can assert the dimension stays a bounded classifier.
    /// </summary>
    /// <remarks>
    /// Deliberately unfiltered by cache name: this instrument only fires while a TLS handshake is
    /// in progress, and this class owns the only TLS Redis in the suite, so there is nothing else in
    /// the process that can produce a measurement to confuse it.
    /// </remarks>
    private sealed class TlsValidationCounter : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
        private readonly List<string> _results = [];
        private readonly object _gate = new();

        public TlsValidationCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Caching.NET.Telemetry.CacheTelemetry.MeterName
                    && instrument.Name == "caching.net.redis.tls.validations")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key != Caching.NET.Telemetry.CacheTelemetryAttributes.Result
                        || tag.Value?.ToString() is not { } result)
                    {
                        continue;
                    }

                    lock (_gate)
                    {
                        _results.Add(result);
                    }
                }
            });

            _listener.Start();
        }

        public IReadOnlyList<string> Results
        {
            get
            {
                lock (_gate)
                {
                    return _results.ToArray();
                }
            }
        }

        public async Task<bool> WaitForAnyAsync(int timeoutMilliseconds = 10_000)
        {
            var deadline = Environment.TickCount64 + timeoutMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (Results.Count > 0)
                {
                    return true;
                }

                await Task.Delay(25);
            }

            return Results.Count > 0;
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task UntrustedTlsCertificate_IsStillRejectedUnderPermissiveValidation()
    {
        await using var provider = Build("redis-tls-permissive", cache => cache
            .UseRedis($"{TlsEndpoint},abortConnect=false")
            // Permissive relaxes a host-name mismatch and nothing else. A certificate signed by
            // nobody the client trusts must stay rejected, or "permissive" would silently mean
            // "accept any endpoint".
            .WithPermissiveRedisTls()
            .WithResilience(r => r.ThrowOnDistributedCacheErrors = true));

        var cache = provider.GetRequiredService<IFusionCache>();

        await Assert.ThrowsAnyAsync<Exception>(async () => await cache.SetAsync("Order:5", 5));
    }

    [Fact]
    public async Task TlsRejection_DoesNotTakeTheApplicationDown()
    {
        await using var provider = Build("redis-tls-degraded", cache => cache
            .UseHybrid($"{TlsEndpoint},abortConnect=false", enableBackplane: false)
            .WithStrictRedisTls());

        var cache = provider.GetRequiredService<IFusionCache>();

        // Hybrid with an unusable L2: L1 and the factory still serve the request.
        Assert.Equal(9, await cache.GetOrSetAsync<int>("Order:6", async _ => 9));
        Assert.Equal(9, await cache.GetOrDefaultAsync<int>("Order:6"));
    }
}
