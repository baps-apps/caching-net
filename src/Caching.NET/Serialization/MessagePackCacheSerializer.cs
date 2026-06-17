using MessagePack;
using MessagePack.Resolvers;

namespace Caching.NET.Serialization;

/// <summary>
/// MessagePack-backed <see cref="ICacheSerializer"/> implementation. Uses the
/// contractless resolver by default so consumer DTOs do not need
/// <c>[MessagePackObject]</c> attributes; pass a custom <see cref="MessagePackSerializerOptions"/>
/// via the constructor when an explicit resolver chain is needed (e.g. for AOT scenarios).
/// </summary>
public sealed class MessagePackCacheSerializer : ICacheSerializer
{
    private readonly MessagePackSerializerOptions _options;

    /// <summary>Construct with the contractless resolver (no attribute requirement on DTOs).</summary>
    public MessagePackCacheSerializer()
        : this(MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance)) { }

    /// <summary>Construct with custom MessagePack options.</summary>
    public MessagePackCacheSerializer(MessagePackSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string FormatId => "msgpack";

    /// <inheritdoc />
    public byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value, _options);

    /// <inheritdoc />
    public T? Deserialize<T>(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty) return default;
        return MessagePackSerializer.Deserialize<T>(bytes, _options);
    }

    /// <inheritdoc />
    public object? Deserialize(ReadOnlyMemory<byte> bytes, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (bytes.IsEmpty) return null;
        return MessagePackSerializer.Deserialize(type, bytes, _options);
    }
}
