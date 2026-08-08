using System.Text;

namespace Caching.NET.Keys;

/// <summary>
/// Builds canonical, validated cache keys. Every segment is checked for whitespace and for the
/// reserved <c>':'</c> separator so that a caller-supplied identifier cannot forge an extra key
/// segment and collide with, or read, another entity's entry.
/// </summary>
public sealed class CacheKeyBuilder
{
    /// <summary>Maximum length of the key produced by <see cref="Build"/>, excluding the cache prefix.</summary>
    public const int MaximumLength = 256;

    private readonly string _typeName;
    private readonly string _id;
    private readonly List<string> _segments = new(2);

    internal CacheKeyBuilder(string typeName, string id)
    {
        _typeName = typeName;
        _id = id;
    }

    /// <summary>Appends a variant segment such as a projection shape or schema version.</summary>
    /// <param name="variant">The variant discriminator.</param>
    public CacheKeyBuilder WithVariant(string variant) => WithSegment(variant);

    /// <summary>Appends a tenant segment. Use this in multi-tenant processes where the tenant cannot be a static prefix.</summary>
    /// <param name="tenantId">The tenant discriminator.</param>
    public CacheKeyBuilder WithTenant(string tenantId) => WithSegment(tenantId);

    /// <summary>Appends an arbitrary segment.</summary>
    /// <param name="segment">The segment text. Must be non-empty and free of whitespace and <c>':'</c>.</param>
    public CacheKeyBuilder WithSegment(string segment)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment);
        _segments.Add(segment);
        return this;
    }

    /// <summary>
    /// Builds the final key.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A segment contains whitespace or <c>':'</c>, or the key exceeds <see cref="MaximumLength"/>.
    /// </exception>
    public string Build()
    {
        ValidateSegment(_typeName);
        ValidateSegment(_id);
        foreach (var segment in _segments)
        {
            ValidateSegment(segment);
        }

        var length = _typeName.Length + 1 + _id.Length;
        for (var i = 0; i < _segments.Count; i++)
        {
            length += 1 + _segments[i].Length;
        }

        if (length > MaximumLength)
        {
            throw new ArgumentException(
                $"Cache key length ({length}) exceeds the {MaximumLength}-character limit. Shorten the identifier or hash it before building the key.");
        }

        var builder = new StringBuilder(length);
        builder.Append(_typeName).Append(':').Append(_id);
        foreach (var segment in _segments)
        {
            builder.Append(':').Append(segment);
        }

        return builder.ToString();
    }

    private static void ValidateSegment(string segment)
    {
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == ':' || char.IsWhiteSpace(c) || char.IsControl(c))
            {
                throw new ArgumentException(
                    $"Cache key segment '{segment}' contains a forbidden character ('{c}'). Segments must not contain ':', whitespace or control characters.");
            }
        }
    }
}
