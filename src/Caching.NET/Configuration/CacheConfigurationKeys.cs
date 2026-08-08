namespace Caching.NET.Configuration;

/// <summary>
/// Configuration section keys for Caching.NET.
/// </summary>
public static class CacheConfigurationKeys
{
    /// <summary>
    /// Root configuration section for Caching.NET, for example
    /// <c>configuration.GetSection(CacheConfigurationKeys.Caching)</c>.
    /// </summary>
    public const string CacheOptions = "CacheOptions";

    /// <summary>
    /// Child section under <see cref="CacheOptions"/> holding additional named caches,
    /// keyed by cache name (for example <c>CacheOptions:NamedCaches:short-lived</c>).
    /// </summary>
    public const string NamedCaches = "NamedCaches";
}
