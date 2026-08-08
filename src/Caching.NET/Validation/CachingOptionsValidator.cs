using System.Text.RegularExpressions;
using Caching.NET.Internal;
using Caching.NET.Options;
using Microsoft.Extensions.Options;

namespace Caching.NET.Validation;

/// <summary>
/// Startup validation for <see cref="CachingOptions"/>. Runs via <c>ValidateOnStart</c>, so a
/// misconfigured cache fails the host at boot rather than at the first request.
/// </summary>
/// <remarks>
/// Error messages name the offending property and the fix. Nothing derived from the Redis
/// connection string is echoed back except through the redactor, so a validation failure cannot
/// leak a password into logs or a crash dump.
/// </remarks>
public sealed partial class CachingOptionsValidator : IValidateOptions<CachingOptions>
{
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CachingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // A disabled cache has no backend to misconfigure; validating it would block hosts that
        // intentionally ship with caching off and no Redis settings.
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        var label = string.IsNullOrEmpty(name) ? CachingDefaults.DefaultCacheName : name;

        ValidateNames(options, label, failures);
        ValidateExpirations(options, failures);
        ValidateResilience(options, failures);
        ValidateRedis(options, failures);
        ValidateBackplane(options, failures);
        ValidateSerialization(options, failures);
        ValidateSecurity(options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures.Select(f => $"Caching[{label}]: {f}"));
    }

    private static void ValidateNames(CachingOptions options, string label, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.CacheName))
        {
            failures.Add("CacheName must not be empty. Set CacheOptions:CacheName or pass a name to AddCaching.");
        }
        else if (options.CacheName.Length > CachingDefaults.MaximumCacheNameLength)
        {
            failures.Add($"CacheName '{options.CacheName}' exceeds {CachingDefaults.MaximumCacheNameLength} characters.");
        }
        else if (!IdentifierPattern().IsMatch(options.CacheName))
        {
            failures.Add($"CacheName '{options.CacheName}' is invalid. Use letters, digits, '.', '_' or '-', starting with a letter or digit.");
        }
        else if (!string.IsNullOrEmpty(label) && !string.Equals(options.CacheName, label, StringComparison.Ordinal))
        {
            failures.Add($"CacheName '{options.CacheName}' does not match the registered cache name '{label}'. Remove CacheName from configuration, or register the cache under the same name.");
        }

        if (string.IsNullOrWhiteSpace(options.ApplicationPrefix))
        {
            failures.Add("ApplicationPrefix is required. It namespaces every key so that applications sharing a Redis database cannot read or invalidate each other's entries.");
        }

        ValidatePrefixSegment(options.ApplicationPrefix, nameof(options.ApplicationPrefix), failures);
        ValidatePrefixSegment(options.EnvironmentPrefix, nameof(options.EnvironmentPrefix), failures);
        ValidatePrefixSegment(options.TenantPrefix, nameof(options.TenantPrefix), failures);
    }

    private static void ValidatePrefixSegment(string? value, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            failures.Add($"{propertyName} '{value}' must not contain ':' — that character separates the prefix from the cache key.");
        }
        else if (!IdentifierPattern().IsMatch(value))
        {
            failures.Add($"{propertyName} '{value}' is invalid. Use letters, digits, '.', '_' or '-', starting with a letter or digit.");
        }
    }

    private static void ValidateExpirations(CachingOptions options, List<string> failures)
    {
        if (options.DefaultExpiration <= TimeSpan.Zero)
        {
            failures.Add($"DefaultExpiration must be greater than zero (was {options.DefaultExpiration}).");
        }

        var entry = options.Entry;

        if (entry.DistributedExpiration is { } distributed && distributed <= TimeSpan.Zero)
        {
            failures.Add($"Entry.DistributedExpiration must be greater than zero when set (was {distributed}).");
        }

        if (entry.LocalExpiration is { } local && local <= TimeSpan.Zero)
        {
            failures.Add($"Entry.LocalExpiration must be greater than zero when set (was {local}).");
        }

        if (entry.EagerRefreshThreshold is { } threshold && (threshold <= 0f || threshold >= 1f))
        {
            failures.Add($"Entry.EagerRefreshThreshold must be between 0 and 1 exclusive when set (was {threshold}).");
        }

        if (entry.JitterMaxDuration < TimeSpan.Zero)
        {
            failures.Add($"Entry.JitterMaxDuration must not be negative (was {entry.JitterMaxDuration}).");
        }

        // Hybrid only: in the other modes one of the two layers is switched off, so the pair cannot
        // disagree. An L1 lifetime longer than the L2 lifetime means this instance keeps serving a
        // value after the authoritative copy is gone from Redis — invisible in a single-instance
        // test and, in a multi-pod deployment, a split view where one pod answers with data every
        // other pod has already refetched.
        if (options.Mode == CacheMode.Hybrid)
        {
            var effectiveLocal = entry.LocalExpiration ?? options.DefaultExpiration;
            var effectiveDistributed = entry.DistributedExpiration ?? options.DefaultExpiration;

            if (effectiveLocal > effectiveDistributed)
            {
                failures.Add($"Entry.LocalExpiration ({effectiveLocal}) is longer than Entry.DistributedExpiration ({effectiveDistributed}), so the in-process copy would outlive the authoritative Redis entry and this instance would serve data no other instance can see. Lower Entry.LocalExpiration, or raise Entry.DistributedExpiration.");
            }
        }

        if (entry.MemorySizeLimitMegabytes is { } limit)
        {
            if (limit <= 0)
            {
                failures.Add($"Entry.MemorySizeLimitMegabytes must be greater than zero when set (was {limit}).");
            }
            else if (entry.Size is null)
            {
                failures.Add("Entry.MemorySizeLimitMegabytes is set but Entry.Size is not. Without a default size, entries are rejected by the memory layer.");
            }
        }
    }

    private static void ValidateResilience(CachingOptions options, List<string> failures)
    {
        var resilience = options.Resilience;

        if (resilience.FailSafeEnabled && resilience.FailSafeMaxDuration < options.DefaultExpiration)
        {
            failures.Add($"Resilience.FailSafeMaxDuration ({resilience.FailSafeMaxDuration}) is shorter than DefaultExpiration ({options.DefaultExpiration}), so an entry would stop being usable as a fail-safe fallback before it even expires.");
        }

        if (resilience.FailSafeEnabled && resilience.FailSafeThrottleDuration <= TimeSpan.Zero)
        {
            failures.Add($"Resilience.FailSafeThrottleDuration must be greater than zero (was {resilience.FailSafeThrottleDuration}).");
        }

        if (resilience.FactoryHardTimeout <= TimeSpan.Zero)
        {
            failures.Add($"Resilience.FactoryHardTimeout must be greater than zero (was {resilience.FactoryHardTimeout}).");
        }

        if (IsFinite(resilience.FactorySoftTimeout)
            && IsFinite(resilience.FactoryHardTimeout)
            && resilience.FactorySoftTimeout > resilience.FactoryHardTimeout)
        {
            failures.Add($"Resilience.FactorySoftTimeout ({resilience.FactorySoftTimeout}) must not exceed Resilience.FactoryHardTimeout ({resilience.FactoryHardTimeout}).");
        }

        if (IsFinite(resilience.DistributedSoftTimeout)
            && IsFinite(resilience.DistributedHardTimeout)
            && resilience.DistributedSoftTimeout > resilience.DistributedHardTimeout)
        {
            failures.Add($"Resilience.DistributedSoftTimeout ({resilience.DistributedSoftTimeout}) must not exceed Resilience.DistributedHardTimeout ({resilience.DistributedHardTimeout}).");
        }

        if (resilience.AutoRecoveryMaxItems is { } maxItems && maxItems <= 0)
        {
            failures.Add($"Resilience.AutoRecoveryMaxItems must be greater than zero when set (was {maxItems}).");
        }

        if (resilience.AutoRecoveryMaxRetryCount is { } maxRetries && maxRetries <= 0)
        {
            failures.Add($"Resilience.AutoRecoveryMaxRetryCount must be greater than zero when set (was {maxRetries}).");
        }
    }

    private static void ValidateRedis(CachingOptions options, List<string> failures)
    {
        var redis = options.Redis;

        if (options.UsesDistributedLayer
            && string.IsNullOrWhiteSpace(redis.Configuration)
            && redis.ConfigureConnection is null)
        {
            failures.Add($"Mode is {options.Mode} but Redis.Configuration is not set. Provide a connection string, supply Redis.ConfigureConnection in code, or switch to Mode=InMemory.");
        }

        if (!options.UsesDistributedLayer && !string.IsNullOrWhiteSpace(redis.Configuration))
        {
            failures.Add($"Mode is {options.Mode} but Redis.Configuration is set. Remove it, or switch to Mode=Redis or Mode=Hybrid — InMemory mode never opens a Redis connection.");
        }

        if (redis.Database is { } database && database < 0)
        {
            failures.Add($"Redis.Database must not be negative (was {database}).");
        }

        if (redis.ConnectTimeout <= TimeSpan.Zero)
        {
            failures.Add($"Redis.ConnectTimeout must be greater than zero (was {redis.ConnectTimeout}).");
        }

        if (redis.CommandTimeout <= TimeSpan.Zero)
        {
            failures.Add($"Redis.CommandTimeout must be greater than zero (was {redis.CommandTimeout}).");
        }

        if (redis.ConnectRetry <= 0)
        {
            failures.Add($"Redis.ConnectRetry must be greater than zero (was {redis.ConnectRetry}).");
        }

        if (options.UsesDistributedLayer
            && !redis.StrictCertificateValidation
            && !UsesTls(redis))
        {
            failures.Add("Redis.StrictCertificateValidation is disabled but TLS is not enabled, so the setting has no effect. Remove it, or enable Redis.UseTls.");
        }

        if (redis.InstancePrefix.Contains(' ', StringComparison.Ordinal))
        {
            failures.Add($"Redis.InstancePrefix '{redis.InstancePrefix}' must not contain whitespace.");
        }
    }

    private static void ValidateBackplane(CachingOptions options, List<string> failures)
    {
        if (!options.Backplane.Enabled)
        {
            return;
        }

        if (!options.UsesDistributedLayer)
        {
            failures.Add($"Backplane.Enabled is true but Mode is {options.Mode}. The backplane needs a Redis connection; switch to Mode=Hybrid or disable the backplane.");
        }
        else if (options.Mode == CacheMode.Redis)
        {
            failures.Add("Backplane.Enabled is true but Mode is Redis, which keeps no local entries to invalidate. Disable the backplane, or switch to Mode=Hybrid.");
        }

        if (options.Backplane.ChannelPrefix is { } channelPrefix
            && channelPrefix.Length > 0
            && channelPrefix.Contains(' ', StringComparison.Ordinal))
        {
            failures.Add($"Backplane.ChannelPrefix '{channelPrefix}' must not contain whitespace.");
        }
    }

    private static void ValidateSerialization(CachingOptions options, List<string> failures)
    {
        var serialization = options.Serialization;

        if (serialization.MaximumPayloadBytes <= 0)
        {
            failures.Add($"Serialization.MaximumPayloadBytes must be greater than zero (was {serialization.MaximumPayloadBytes}).");
        }

        if (!Enum.IsDefined(serialization.Format))
        {
            failures.Add($"Serialization.Format '{serialization.Format}' is not a supported format.");
        }

        var compression = serialization.Compression;
        if (!compression.Enabled)
        {
            return;
        }

        if (compression.ThresholdBytes < 0)
        {
            failures.Add($"Serialization.Compression.ThresholdBytes must not be negative (was {compression.ThresholdBytes}).");
        }

        if (compression.MaximumDecompressedBytes <= 0)
        {
            failures.Add($"Serialization.Compression.MaximumDecompressedBytes must be greater than zero (was {compression.MaximumDecompressedBytes}). This is the decompression-bomb ceiling.");
        }
        else if (compression.MaximumDecompressedBytes < serialization.MaximumPayloadBytes)
        {
            failures.Add($"Serialization.Compression.MaximumDecompressedBytes ({compression.MaximumDecompressedBytes}) is below Serialization.MaximumPayloadBytes ({serialization.MaximumPayloadBytes}), so a legitimate entry at the size limit would be rejected on read.");
        }
    }

    private static void ValidateSecurity(CachingOptions options, List<string> failures)
    {
        var security = options.Security;

        if (security.MaximumKeyLength <= 0)
        {
            failures.Add($"Security.MaximumKeyLength must be greater than zero (was {security.MaximumKeyLength}).");
        }
        else
        {
            var prefixLength = options.BuildKeyPrefix().Length;
            if (prefixLength > 0 && prefixLength + 1 >= security.MaximumKeyLength)
            {
                failures.Add($"Security.MaximumKeyLength ({security.MaximumKeyLength}) leaves no room for a cache key after the {prefixLength}-character prefix.");
            }
        }

        if (security.MaximumTagLength <= 0)
        {
            failures.Add($"Security.MaximumTagLength must be greater than zero (was {security.MaximumTagLength}).");
        }

        if (security.MaximumTagCount <= 0)
        {
            failures.Add($"Security.MaximumTagCount must be greater than zero (was {security.MaximumTagCount}).");
        }
    }

    private static bool UsesTls(RedisOptions redis)
        => redis.UseTls
            || (redis.Configuration?.Contains("ssl=true", StringComparison.OrdinalIgnoreCase) ?? false)
            || redis.ConfigureConnection is not null;

    private static bool IsFinite(TimeSpan value) => value != Timeout.InfiniteTimeSpan && value >= TimeSpan.Zero;
}
