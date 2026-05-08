using System;

namespace Caching.NET;

/// <summary>
/// Optional explicit schema version mixed into the Redis <see cref="Serialization.PayloadEnvelope"/> schema hash.
/// Use when you change serialized shape and need cache invalidation without renaming the CLR type.
/// </summary>
/// <remarks>
/// By default the envelope hashes <see cref="Type.FullName"/> only so upgrading <c>Caching.NET</c> or assembly
/// file version does not invalidate entries. Opt into this attribute when you intentionally bump compatibility.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum, Inherited = false)]
public sealed class CacheSchemaAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CacheSchemaAttribute"/> class.
    /// </summary>
    /// <param name="version">Non-empty logical schema label (e.g. <c>"v2"</c>).</param>
    public CacheSchemaAttribute(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Version = version;
    }

    /// <summary>Logical schema version included in the envelope schema hash.</summary>
    public string Version { get; }
}
