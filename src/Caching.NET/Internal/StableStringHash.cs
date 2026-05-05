using System.Text;

namespace Caching.NET.Internal;

/// <summary>
/// Process- and machine-stable xxHash32 over a UTF-8 view of a string.
/// Used for striped-lock placement so the same key always maps to the same stripe
/// across process restarts and across machines (unlike <see cref="string.GetHashCode()"/>,
/// which is randomized per process).
/// </summary>
internal static class StableStringHash
{
    private const uint Prime1 = 2654435761u;
    private const uint Prime2 = 2246822519u;
    private const uint Prime3 = 3266489917u;
    private const uint Prime4 = 668265263u;
    private const uint Prime5 = 374761393u;

    public static uint Compute(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var byteCount = Encoding.UTF8.GetByteCount(input);
        Span<byte> buffer = byteCount <= 256
            ? stackalloc byte[256]
            : new byte[byteCount];
        var written = Encoding.UTF8.GetBytes(input, buffer);
        return Compute(buffer[..written]);
    }

    public static uint Compute(ReadOnlySpan<byte> data) => unchecked(ComputeCore(data));

    private static uint ComputeCore(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            var len = (uint)data.Length;
            uint h32;
            var index = 0;

            if (data.Length >= 16)
            {
                uint v1 = Prime1 + Prime2;
                uint v2 = Prime2;
                uint v3 = 0;
                uint v4 = 0u - Prime1;

                do
                {
                    v1 = Round(v1, BitConverter.ToUInt32(data.Slice(index, 4))); index += 4;
                    v2 = Round(v2, BitConverter.ToUInt32(data.Slice(index, 4))); index += 4;
                    v3 = Round(v3, BitConverter.ToUInt32(data.Slice(index, 4))); index += 4;
                    v4 = Round(v4, BitConverter.ToUInt32(data.Slice(index, 4))); index += 4;
                } while (data.Length - index >= 16);

                h32 = RotL(v1, 1) + RotL(v2, 7) + RotL(v3, 12) + RotL(v4, 18);
            }
            else
            {
                h32 = Prime5;
            }

            h32 += len;

            while (data.Length - index >= 4)
            {
                h32 += BitConverter.ToUInt32(data.Slice(index, 4)) * Prime3;
                h32 = RotL(h32, 17) * Prime4;
                index += 4;
            }

            while (index < data.Length)
            {
                h32 += data[index] * Prime5;
                h32 = RotL(h32, 11) * Prime1;
                index++;
            }

            h32 ^= h32 >> 15;
            h32 *= Prime2;
            h32 ^= h32 >> 13;
            h32 *= Prime3;
            h32 ^= h32 >> 16;

            return h32;
        }
    }

    private static uint Round(uint acc, uint input)
    {
        unchecked
        {
            acc += input * Prime2;
            acc = RotL(acc, 13);
            acc *= Prime1;
            return acc;
        }
    }

    private static uint RotL(uint x, int r) => (x << r) | (x >> (32 - r));
}
