namespace Caching.NET.Keys;

/// <summary>
/// Builds canonical, validated cache keys. Each segment is checked for whitespace
/// and the reserved <c>:</c> separator. <c>Build()</c> caps the post-prefix length
/// at 256 characters; the routing layer's <c>KeyPrefix</c> adds further headroom
/// up to <see cref="Options.CacheOptions.MaximumKeyLength"/>.
/// </summary>
public sealed class CacheKeyBuilder
{
    private readonly string _typeName;
    private readonly string _id;
    private readonly List<string> _segments = new(2);

    internal CacheKeyBuilder(string typeName, string id)
    {
        _typeName = typeName;
        _id = id;
    }

    /// <summary>Append a variant segment (e.g. version, view-shape).</summary>
    public CacheKeyBuilder WithVariant(string variant) => WithSegment(variant);

    /// <summary>Append an arbitrary segment.</summary>
    public CacheKeyBuilder WithSegment(string segment)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment);
        _segments.Add(segment);
        return this;
    }

    /// <summary>
    /// Build the final key. Throws <see cref="ArgumentException"/> when any segment
    /// contains whitespace or <c>:</c>, or when the total length exceeds 256.
    /// </summary>
    public string Build()
    {
        ValidateSegment(_typeName);
        ValidateSegment(_id);
        foreach (var s in _segments) ValidateSegment(s);

        var totalLen = _typeName.Length + 1 + _id.Length;
        for (int i = 0; i < _segments.Count; i++) totalLen += 1 + _segments[i].Length;
        if (totalLen > 256) throw new ArgumentException($"Cache key length ({totalLen}) exceeds 256 characters.");

        var sb = new System.Text.StringBuilder(totalLen);
        sb.Append(_typeName).Append(':').Append(_id);
        foreach (var s in _segments) sb.Append(':').Append(s);
        return sb.ToString();
    }

    private static void ValidateSegment(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ':' || char.IsWhiteSpace(c))
                throw new ArgumentException($"Cache key segment '{s}' contains a forbidden character ('{c}'). Use only ASCII letters/digits/dot/underscore/dash.");
        }
    }
}
