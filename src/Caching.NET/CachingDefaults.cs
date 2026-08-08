namespace Caching.NET;

/// <summary>
/// Well-known constants for Caching.NET.
/// </summary>
public static class CachingDefaults
{
    /// <summary>Name of the cache registered by the unnamed <c>AddCaching</c> overloads.</summary>
    public const string DefaultCacheName = "default";

    /// <summary>Separator placed between the key prefix and the caller-supplied key.</summary>
    public const string KeyPrefixSeparator = ":";

    /// <summary>Maximum length of a <see cref="Options.CachingOptions.CacheName"/>.</summary>
    public const int MaximumCacheNameLength = 64;
}
