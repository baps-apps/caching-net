using Caching.NET.Options;
using Caching.NET.Telemetry;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Internal;

/// <summary>
/// Default <see cref="ICacheGuard"/>. One instance per cache, holding that cache's resolved limits.
/// </summary>
internal sealed class CacheGuard : ICacheGuard
{
    private readonly CacheSecurityOptions _security;
    private readonly CacheTelemetryContext _telemetry;
    private readonly ILogger _logger;
    private readonly int _prefixLength;

    public CacheGuard(CachingOptions options, CacheTelemetryContext telemetry, ILogger logger)
    {
        CacheName = options.CacheName;
        _security = options.Security;
        _telemetry = telemetry;
        _logger = logger;

        var prefix = options.BuildKeyPrefix();
        _prefixLength = prefix.Length == 0 ? 0 : prefix.Length + CachingDefaults.KeyPrefixSeparator.Length;
    }

    public string CacheName { get; }

    public void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ValidateKeyLength(key, _prefixLength + key.Length);
    }

    /// <summary>
    /// Validates a key that the engine has already prefixed. Called from the engine's per-operation
    /// hook, where the key is the full physical key.
    /// </summary>
    public void ValidatePhysicalKey(string physicalKey) => ValidateKeyLength(physicalKey, physicalKey.Length);

    private void ValidateKeyLength(string key, int physicalLength)
    {
        if (_security.KeyLengthPolicy == CacheGuardPolicy.Ignore || physicalLength <= _security.MaximumKeyLength)
        {
            return;
        }

        _telemetry.RecordGuardViolation("key_too_long");

        if (_security.KeyLengthPolicy == CacheGuardPolicy.Throw)
        {
            // The exception itself reports the breach; logging it as well would report the same
            // defect twice for callers who let it propagate.
            throw new ArgumentException(
                $"Cache key length ({physicalLength} characters including the '{CacheName}' cache prefix) exceeds Security.MaximumKeyLength ({_security.MaximumKeyLength}). Shorten the key, hash the variable part, or raise the limit deliberately.",
                "key");
        }

        // Warn policy: the operation proceeds, so the log entry is the only signal an operator gets.
        // A raw key can carry identifiers, so only a fingerprint is written unless the application
        // has explicitly opted in.
        CacheLogMessages.KeyTooLong(
            _logger,
            CacheName,
            _security.MaximumKeyLength,
            physicalLength,
            _security.AllowRawKeysInLogs ? key : KeyFingerprint.Compute(key));
    }

    public void ValidateTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (_security.TagPolicy == CacheGuardPolicy.Ignore)
        {
            return;
        }

        var count = 0;
        foreach (var tag in tags)
        {
            count++;

            if (string.IsNullOrWhiteSpace(tag))
            {
                Reject("a tag is empty or whitespace");
                continue;
            }

            if (tag.Length > _security.MaximumTagLength)
            {
                Reject($"a tag of {tag.Length} characters exceeds Security.MaximumTagLength ({_security.MaximumTagLength})");
            }
        }

        if (count > _security.MaximumTagCount)
        {
            Reject($"{count} tags exceed Security.MaximumTagCount ({_security.MaximumTagCount})");
        }
    }

    public string Fingerprint(string key) => KeyFingerprint.Compute(key);

    private void Reject(string reason)
    {
        _telemetry.RecordGuardViolation("tag_rejected");
        CacheLogMessages.TagRejected(_logger, CacheName, reason);

        if (_security.TagPolicy == CacheGuardPolicy.Throw)
        {
            throw new ArgumentException($"Cache tag rejected on cache '{CacheName}': {reason}.", "tags");
        }
    }
}
