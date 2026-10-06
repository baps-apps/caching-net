namespace Caching.NET.Options;

/// <summary>
/// Where the cache engine's wire-format version appears in a physical Redis key.
/// </summary>
/// <remarks>
/// <para>
/// The engine can write the version of its serialized-entry format into every distributed key —
/// <c>v2</c> in the current release. Its purpose is isolation across engine upgrades: a release that
/// changes the stored format also changes the version, so new code reads a fresh key space and never
/// attempts to decode an entry written in the old format. The old entries expire by their TTL.
/// </para>
/// <para>
/// With <see cref="None"/> that isolation is gone. After an engine upgrade that changes the format,
/// the new code reads entries written in the old one, under the same keys, until they expire or are
/// overwritten. Check the engine's release notes for a format change before taking such an upgrade,
/// and flush or wait out the distributed TTL if there is one.
/// </para>
/// <para>
/// <b>Changing this value changes every physical key.</b> Entries written under the previous
/// placement become unreachable and expire by TTL, so the change deploys as a cold cache. While old
/// and new replicas run side by side they read and write different keys for the same entry, and an
/// invalidation issued by one side does not delete the other side's copy: each side can serve a stale
/// value until its distributed expiration. Roll the change out with a recreate deployment, or accept
/// that window.
/// </para>
/// <para>
/// Applies only to the distributed layer (<see cref="CacheMode.Redis"/> and
/// <see cref="CacheMode.Hybrid"/>). <see cref="RedisOptions.InstancePrefix"/> is always applied
/// outside the whole key, version segment included.
/// </para>
/// </remarks>
public enum CacheKeyVersionPlacement
{
    /// <summary>
    /// No version segment: <c>orders-api:prod:Order:1</c>. The default, so a Redis key starts with
    /// the application prefix and reads exactly as the caller's key composition describes it.
    /// </summary>
    None,

    /// <summary>
    /// Version in front of the whole key: <c>v2:orders-api:prod:Order:1</c>. The layout Caching.NET
    /// 3.0.0 through 3.1.1 always wrote — set this to keep reading the entries they wrote.
    /// </summary>
    Prefix,

    /// <summary>
    /// Version after the caller's key: <c>orders-api:prod:Order:1:v2</c>. Keeps the upgrade isolation
    /// while still letting an application-prefix scan (<c>orders-api:*</c>) find every key.
    /// </summary>
    Suffix
}
