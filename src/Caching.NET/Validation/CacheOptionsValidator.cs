using System.Text.RegularExpressions;
using Caching.NET.Internal;
using Caching.NET.Options;
using Microsoft.Extensions.Options;

namespace Caching.NET.Validation;

/// <summary>
/// Validates <see cref="CacheOptions"/> at startup. Replaces v1's DataAnnotations-only validation
/// (which was trim/AOT-unsafe) with explicit checks that include redacted connection strings in
/// failure messages. Wire via <c>services.AddSingleton&lt;IValidateOptions&lt;CacheOptions&gt;&gt;()</c>
/// alongside <c>.ValidateOnStart()</c>.
/// </summary>
internal sealed partial class CacheOptionsValidator : IValidateOptions<CacheOptions>
{
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPrefixRegex();

    public ValidateOptionsResult Validate(string? name, CacheOptions o)
    {
        // Skip validation when disabled — keeps zero-config and disabled-with-bad-prod-config scenarios working.
        if (!o.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

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
        else if (!KeyPrefixRegex().IsMatch(o.KeyPrefix))
        {
            failures.Add($"{nameof(CacheOptions.KeyPrefix)} must match ^[a-zA-Z0-9][a-zA-Z0-9._:-]*$ (no whitespace, '*' or '?').");
        }

        // Redis mode strictly requires a connection string. Hybrid tolerates a missing connection
        // string and runs as in-memory-only Hybrid (matches v1 ergonomics for tests/local dev).
        if (o.Mode == CacheMode.Redis && string.IsNullOrWhiteSpace(o.RedisConnectionString))
        {
            failures.Add($"{nameof(CacheOptions.RedisConnectionString)} is required for Mode=Redis. (redacted={redactedCs})");
        }

        if (o.MaximumKeyLength is < 64 or > 8192)
        {
            failures.Add($"{nameof(CacheOptions.MaximumKeyLength)} must be in [64, 8192]. (redacted={redactedCs})");
        }

        if (o.MaximumPayloadBytes is < 1024L or > 100L * 1024 * 1024)
        {
            failures.Add($"{nameof(CacheOptions.MaximumPayloadBytes)} must be in [1024, 104857600].");
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
