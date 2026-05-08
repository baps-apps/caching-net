namespace Caching.NET.Internal;

internal static class RedisConnectionStringRedactor
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "user", "name"
    };

    public static string Redact(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return string.Empty;
        var parts = connectionString.Split(',');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            var key = parts[i][..eq].Trim();
            if (SecretKeys.Contains(key))
            {
                parts[i] = $"{key}=***";
            }
        }
        return string.Join(",", parts);
    }
}
