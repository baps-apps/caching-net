using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caching.NET.Serialization;

/// <summary>
/// Default <see cref="ICacheSerializer"/> using <see cref="System.Text.Json"/>.
/// Consumers may pass their own <see cref="JsonSerializerContext"/> for AOT/trim
/// compatibility; otherwise reflection-based STJ is used (incompatible with full trim).
/// IL2026/IL3050 warnings on the reflection path are suppressed at csproj level until
/// P2 ships a source-gen-aware default and consumers opt in.
/// </summary>
public sealed class JsonCacheSerializer : ICacheSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Construct with web defaults (camelCase, case-insensitive).</summary>
    public JsonCacheSerializer() : this(new JsonSerializerOptions(JsonSerializerDefaults.Web))
    {
    }

    /// <summary>Construct with explicit serializer options.</summary>
    public JsonCacheSerializer(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Construct from a source-gen <see cref="JsonSerializerContext"/> for AOT/trim safety.</summary>
    public JsonCacheSerializer(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _options = new JsonSerializerOptions(context.Options);
    }

    /// <inheritdoc />
    public string FormatId => "json";

    /// <inheritdoc />
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, _options);

    /// <inheritdoc />
    public T? Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return default;
        return JsonSerializer.Deserialize<T>(bytes, _options);
    }
}
