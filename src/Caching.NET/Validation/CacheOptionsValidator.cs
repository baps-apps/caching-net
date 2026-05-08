using System.Text.RegularExpressions;
using Caching.NET.Internal;
using Caching.NET.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Caching.NET.Validation;

/// <summary>
/// Validates <see cref="CacheOptions"/> at startup. Replaces v1's DataAnnotations-only validation
/// (which was trim/AOT-unsafe) with explicit checks that include redacted connection strings in
/// failure messages. Wire via <c>services.AddSingleton&lt;IValidateOptions&lt;CacheOptions&gt;&gt;()</c>
/// alongside <c>.ValidateOnStart()</c>.
/// </summary>
internal sealed class CacheOptionsValidator : IValidateOptions<CacheOptions>
{
    /// <summary>
    /// Culture-invariant pattern used to validate <see cref="CacheOptions.KeyPrefix"/> after ':' and length checks.
    /// </summary>
    /// <remarks>
    /// Colon is forbidden in <see cref="CacheOptions.KeyPrefix"/> — routing uses a single ASCII ':' between prefix and user key;
    /// allowing ':' inside the prefix creates ambiguous physical keys (see audit B6).
    /// Pattern: <c>^[a-zA-Z0-9][a-zA-Z0-9._-]*$</c>.
    /// </remarks>
    private static readonly Regex KeyPrefixRegex = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9._-]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    public ValidateOptionsResult Validate(string? name, CacheOptions o)
    {
        // When the cache is disabled, no backend is registered and the routing service
        // short-circuits every call to the factory. Validating a disabled configuration
        // would force callers to keep a valid Redis connection string in the disabled
        // appsettings, defeating the point of the toggle. Skip.
        if (!o.Enabled) return ValidateOptionsResult.Success;

        var failures = new List<string>();
        var redactedCs = RedisConnectionStringRedactor.Redact(o.RedisConnectionString);

        if (string.IsNullOrWhiteSpace(o.KeyPrefix))
        {
            failures.Add($"{nameof(CacheOptions.KeyPrefix)} is required and must be non-empty.");
        }
        else if (o.KeyPrefix.Length > 64)
        {
            failures.Add($"{nameof(CacheOptions.KeyPrefix)} must be <= 64 chars.");
        }
        else if (o.KeyPrefix.Contains(':', StringComparison.Ordinal))
        {
            failures.Add(
                $"{nameof(CacheOptions.KeyPrefix)} must not contain ':' (reserved as the delimiter between prefix and user keys).");
        }
        else if (!KeyPrefixRegex.IsMatch(o.KeyPrefix))
        {
            failures.Add(
                $"{nameof(CacheOptions.KeyPrefix)} must match ^[a-zA-Z0-9][a-zA-Z0-9._-]*$ (no whitespace, ':' , '*' or '?').");
        }

        if ((o.Mode == CacheMode.Redis || o.Mode == CacheMode.Hybrid) && string.IsNullOrWhiteSpace(o.RedisConnectionString))
        {
            failures.Add($"{nameof(CacheOptions.RedisConnectionString)} is required for Mode={o.Mode}. (redacted={redactedCs})");
        }

        if ((o.Mode == CacheMode.Redis || o.Mode == CacheMode.Hybrid) && !string.IsNullOrWhiteSpace(o.RedisConnectionString))
        {
            try
            {
                var parsed = ConfigurationOptions.Parse(o.RedisConnectionString!, ignoreUnknown: true);
                if (parsed.EndPoints.Count == 0)
                {
                    failures.Add($"{nameof(CacheOptions.RedisConnectionString)} did not resolve any Redis endpoints. (redacted={redactedCs})");
                }
            }
            catch (Exception)
            {
                failures.Add($"{nameof(CacheOptions.RedisConnectionString)} could not be parsed as a Redis configuration. (redacted={redactedCs})");
            }
        }

        if (o.MaximumKeyLength is < 64 or > 8192)
        {
            failures.Add($"{nameof(CacheOptions.MaximumKeyLength)} must be in [64, 8192]. (redacted={redactedCs})");
        }

        if (o.MaximumKeyLength is >= 64 and <= 8192
            && !string.IsNullOrWhiteSpace(o.KeyPrefix)
            && KeyPrefixRegex.IsMatch(o.KeyPrefix)
            && o.KeyPrefix.Length <= 64
            && o.MaximumKeyLength > 0
            && o.KeyPrefix.Length + 1 + 32 > o.MaximumKeyLength)
        {
            failures.Add(
                $"{nameof(CacheOptions.MaximumKeyLength)} must leave at least 32 characters for user keys after " +
                $"{nameof(CacheOptions.KeyPrefix)} and the ':' separator (prefix consumes {o.KeyPrefix.Length + 1} of {o.MaximumKeyLength}).");
        }

        if (o.MaximumPayloadBytes is < 1024L or > 100L * 1024 * 1024)
        {
            failures.Add($"{nameof(CacheOptions.MaximumPayloadBytes)} must be in [1024, 104857600].");
        }

        if (o.PayloadCompressionThresholdBytes is < 256 or > 100 * 1024 * 1024)
        {
            failures.Add($"{nameof(CacheOptions.PayloadCompressionThresholdBytes)} must be in [256, 104857600].");
        }

        if (o.StripeLockCount is < 16 or > 65536)
        {
            failures.Add($"{nameof(CacheOptions.StripeLockCount)} must be in [16, 65536].");
        }

        if (o.TtlJitterPercentage is < 0 or > 0.5)
        {
            failures.Add($"{nameof(CacheOptions.TtlJitterPercentage)} must be in [0, 0.5].");
        }

        if (o.RequireTagSupport && o.Mode != CacheMode.Hybrid)
        {
            failures.Add($"RequireTagSupport() was called but Mode={o.Mode}; tag support is only available in Hybrid mode.");
        }

        if (o.FactoryTimeout < TimeSpan.FromMilliseconds(100) || o.FactoryTimeout > TimeSpan.FromMinutes(30))
        {
            failures.Add($"{nameof(CacheOptions.FactoryTimeout)} must be between 100ms and 30 minutes.");
        }

        if (o.RedisOperationTimeout < TimeSpan.FromMilliseconds(50) || o.RedisOperationTimeout > TimeSpan.FromSeconds(30))
        {
            failures.Add($"{nameof(CacheOptions.RedisOperationTimeout)} must be between 50ms and 30s.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
