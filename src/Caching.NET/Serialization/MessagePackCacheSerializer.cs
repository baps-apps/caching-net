using System.Buffers;
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
    public T? Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return default;
        var seq = new ReadOnlySequence<byte>(bytes.ToArray());
        return MessagePackSerializer.Deserialize<T>(seq, _options);
    }
}
