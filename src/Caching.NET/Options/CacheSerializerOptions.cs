using System.Text.Json;
using System.ComponentModel.DataAnnotations;

namespace Caching.NET.Options;

/// <summary>
/// Optional serialization options for Redis and other distributed cache implementations.
/// Register via DI when you need custom JSON behavior (e.g., naming policy, converters).
/// When not registered, a default serializer is used.
/// </summary>
public class CacheSerializerOptions
{
    /// <summary>
    /// Options used for serializing and deserializing cache values to/from JSON.
    /// Only used when this options type is registered in DI; otherwise the default (case-insensitive) is used.
    /// </summary>
    [Required]
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }
}
