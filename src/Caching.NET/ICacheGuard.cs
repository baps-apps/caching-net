using Caching.NET.Options;

namespace Caching.NET;

/// <summary>
/// Validates cache keys and tags against the limits configured for a cache instance, and produces
/// non-reversible key fingerprints for correlation.
/// </summary>
/// <remarks>
/// <para>
/// Caching.NET applies these same limits itself on every cache call: each key-taking member of
/// <see cref="ICacheService"/> validates its key, and each tag-taking member validates its tags,
/// before the operation reaches the cache — unconditionally, including calls that pass explicit
/// per-entry options. Nothing an application does can bypass that, and it does not have to opt in.
/// </para>
/// <para>
/// Resolve this service from dependency injection when you build keys or tags from user input and
/// want the limit breach to surface at your own boundary — with your own error response — rather
/// than as an exception from a cache call further in.
/// </para>
/// </remarks>
public interface ICacheGuard
{
    /// <summary>Name of the cache instance these limits belong to.</summary>
    string CacheName { get; }

    /// <summary>
    /// Validates a caller-supplied key against <see cref="CacheSecurityOptions.MaximumKeyLength"/>,
    /// accounting for the prefix the cache prepends.
    /// </summary>
    /// <param name="key">The key as passed to the cache.</param>
    /// <exception cref="ArgumentException">
    /// The key is empty, or it breaks a limit and the configured policy is
    /// <see cref="CacheGuardPolicy.Throw"/>.
    /// </exception>
    void ValidateKey(string key);

    /// <summary>
    /// Validates a tag set against <see cref="CacheSecurityOptions.MaximumTagCount"/> and
    /// <see cref="CacheSecurityOptions.MaximumTagLength"/>.
    /// </summary>
    /// <param name="tags">The tags to attach to an entry.</param>
    /// <exception cref="ArgumentException">
    /// A tag breaks a limit and the configured policy is <see cref="CacheGuardPolicy.Throw"/>.
    /// </exception>
    void ValidateTags(IEnumerable<string> tags);

    /// <summary>
    /// Returns a non-reversible 64-bit fingerprint of <paramref name="key"/> as lowercase hex.
    /// Safe to attach to a log entry, span or support ticket; a raw key is not.
    /// </summary>
    /// <param name="key">The key to fingerprint.</param>
    string Fingerprint(string key);
}
