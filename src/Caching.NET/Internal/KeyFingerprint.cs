using System.Buffers;
using System.IO.Hashing;
using System.Text;

namespace Caching.NET.Internal;

/// <summary>
/// Non-reversible, deterministic fingerprint of a cache key. Used wherever a key must be
/// correlated across logs, spans or support tickets without exposing the key itself, which may
/// embed identifiers or other personal data.
/// </summary>
/// <remarks>
/// xxHash64 is a fast non-cryptographic hash. It is not a secret-hiding primitive: it protects
/// against casual disclosure and accidental logging of personal data in telemetry, not against a
/// determined offline attack on a small key space. Never fingerprint a value that is itself a
/// secret.
/// </remarks>
internal static class KeyFingerprint
{
    private const int StackAllocThreshold = 512;

    public static string Compute(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Compute64(key).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static ulong Compute64(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
        {
            return XxHash64.HashToUInt64(ReadOnlySpan<byte>.Empty);
        }

        var byteCount = Encoding.UTF8.GetByteCount(key);
        if (byteCount <= StackAllocThreshold)
        {
            Span<byte> buffer = stackalloc byte[byteCount];
            var written = Encoding.UTF8.GetBytes(key, buffer);
            return XxHash64.HashToUInt64(buffer[..written]);
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(key, rented);
            return XxHash64.HashToUInt64(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
