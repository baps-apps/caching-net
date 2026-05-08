using System.Text.Json.Serialization;

[JsonSerializable(typeof(Order))]
public partial class AppJsonContext : JsonSerializerContext { }
