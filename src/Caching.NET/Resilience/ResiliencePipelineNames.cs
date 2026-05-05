namespace Caching.NET.Resilience;

/// <summary>Stable names for the Caching.NET-managed Polly resilience pipelines.</summary>
public static class ResiliencePipelineNames
{
    /// <summary>Pipeline applied to Redis read operations.</summary>
    public const string RedisRead = "cache.redis.read";

    /// <summary>Pipeline applied to Redis write operations.</summary>
    public const string RedisWrite = "cache.redis.write";

    /// <summary>Pipeline applied to Redis delete operations.</summary>
    public const string RedisDelete = "cache.redis.delete";
}
