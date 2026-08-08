using System.Text.Json.Serialization;

namespace Caching.NET.AotSmoke;

/// <summary>Cached payload used by the AOT smoke test.</summary>
public sealed record Product(int Id, string Name);

/// <summary>
/// Source-generated JSON context. Supplying one of these through
/// <c>Serialization.JsonSerializerOptions.TypeInfoResolver</c> is what makes the distributed layer
/// trim- and AOT-safe.
/// </summary>
[JsonSerializable(typeof(Product))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class AppJsonContext : JsonSerializerContext
{
}
