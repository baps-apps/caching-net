namespace Caching.NET.Internal;

/// <summary>
/// Turns the physical cache key carried by a backplane message back into the key the caller used, so
/// a received message can be tagged with something that correlates with the span that published it.
/// </summary>
/// <remarks>
/// <para>
/// Two decorations sit between the two. The engine prefixes every key with its configured cache-key
/// prefix — Caching.NET's application, environment and tenant prefix — before it publishes, so the
/// wire always carries the physical key while <c>cache.key</c> is defined as the caller's. And a tag
/// invalidation does not travel as itself: the engine implements <c>RemoveByTag</c> and <c>Clear</c>
/// as marker entries under an internal key, so a tag message arrives looking like an entry message
/// for a key no caller ever wrote.
/// </para>
/// <para>
/// Undoing both is what makes the fingerprint useful: it then equals the one on the publishing
/// instance's <c>cache.remove</c> or <c>cache.remove_by_tag</c> span, which is the only correlation
/// available across processes — the message format has no field for trace context. Left undecoded,
/// the value would be a fingerprint of a string no other span ever fingerprints, which is worse than
/// no value at all, because nothing about it says so.
/// </para>
/// <para>
/// <c>Clear</c> arrives as a marker under one of two reserved tags and decodes to no key, matching
/// <c>cache.clear</c> — the one operation span that carries no key, because there is none to carry.
/// </para>
/// <para>
/// The strings this is built from are engine <i>configuration</i> rather than contract, so
/// <c>InstrumentedBackplaneReceiveTests.EngineTagStrings_AreStillWhatTheDecoderIsBuiltFrom</c> pins
/// them: a future engine version that renames the marker prefix would otherwise silently downgrade
/// tag messages to unrecognisable keys rather than fail.
/// </para>
/// </remarks>
internal sealed class BackplaneKeyDecoder
{
    private readonly string _cacheKeyPrefix;
    private readonly string _tagKeyPrefix;
    private readonly string _clearRemoveTag;
    private readonly string _clearExpireTag;

    public BackplaneKeyDecoder(string cacheKeyPrefix, string tagKeyPrefix, string clearRemoveTag, string clearExpireTag)
    {
        _cacheKeyPrefix = cacheKeyPrefix;
        _tagKeyPrefix = tagKeyPrefix;
        _clearRemoveTag = clearRemoveTag;
        _clearExpireTag = clearExpireTag;
    }

    /// <summary>
    /// Recovers the caller-facing key or tag from <paramref name="physicalKey"/>. Returns
    /// <see langword="false"/> when there is nothing a caller would recognise — a <c>Clear</c> marker,
    /// or a key that is nothing but the prefix — in which case no key attribute should be set.
    /// </summary>
    public bool TryDecode(string? physicalKey, out string logicalKey)
    {
        logicalKey = string.Empty;

        if (string.IsNullOrEmpty(physicalKey))
        {
            return false;
        }

        var key = physicalKey.AsSpan();

        if (_cacheKeyPrefix.Length > 0 && key.StartsWith(_cacheKeyPrefix, StringComparison.Ordinal))
        {
            key = key[_cacheKeyPrefix.Length..];
        }

        if (_tagKeyPrefix.Length > 0 && key.StartsWith(_tagKeyPrefix, StringComparison.Ordinal))
        {
            key = key[_tagKeyPrefix.Length..];

            if (key.Equals(_clearRemoveTag, StringComparison.Ordinal)
                || key.Equals(_clearExpireTag, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (key.IsEmpty)
        {
            return false;
        }

        logicalKey = key.ToString();
        return true;
    }
}
