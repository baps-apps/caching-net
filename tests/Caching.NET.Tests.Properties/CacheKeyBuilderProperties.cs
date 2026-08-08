using Caching.NET.Internal;
using Caching.NET.Keys;
using FsCheck;
using FsCheck.Xunit;

namespace Caching.NET.Tests.Properties;

public class CacheKeyBuilderProperties
{
    private static bool IsSafeSegment(string? value)
        => !string.IsNullOrEmpty(value)
            && value.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c != ':');

    [Property(MaxTest = 300)]
    public bool SafeSegments_ProduceAColonDelimitedKey(NonEmptyString id, NonEmptyString segment)
    {
        if (!IsSafeSegment(id.Get) || !IsSafeSegment(segment.Get) || id.Get.Length + segment.Get.Length > 200)
        {
            return true;
        }

        var key = CacheKey.For<CacheKeyBuilderProperties>(id.Get).WithSegment(segment.Get).Build();
        return key == $"{nameof(CacheKeyBuilderProperties)}:{id.Get}:{segment.Get}";
    }

    /// <summary>
    /// Reduces an arbitrary generated string to something the key builder would otherwise accept:
    /// no whitespace, no control characters, no separator, and short enough that the length rule
    /// cannot fire. Empty input becomes a single safe character, so the result is always usable.
    /// </summary>
    private static string ToSafeSegment(string value)
    {
        var safe = new string(value
            .Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c != ':')
            .Take(100)
            .ToArray());

        return safe.Length == 0 ? "x" : safe;
    }

    /// <summary>
    /// The key-injection guard: a caller-supplied identifier containing the reserved <c>':'</c>
    /// must be rejected, or <c>sku = "1:Order:2"</c> mints a key belonging to a different entity.
    /// </summary>
    /// <remarks>
    /// The separator is <b>constructed</b> rather than waited for. This property used to filter on
    /// <c>id.Contains(':')</c>, and FsCheck's <see cref="NonEmptyString"/> generator never produced a
    /// <c>':'</c> in 300 cases, so the precondition was taken every single time and the rejection
    /// below was never reached — deleting the guard from <c>CacheKeyBuilder</c> left this property
    /// green. Both halves are sanitised first so the <see cref="ArgumentException"/> can only be the
    /// forbidden-separator rejection, never the whitespace, control-character or length rule.
    /// </remarks>
    [Property(MaxTest = 300)]
    public bool SegmentsContainingTheSeparator_AreAlwaysRejected(NonEmptyString left, NonEmptyString right)
    {
        var forged = $"{ToSafeSegment(left.Get)}:{ToSafeSegment(right.Get)}";

        try
        {
            CacheKey.For<CacheKeyBuilderProperties>(forged).Build();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Property(MaxTest = 300)]
    public bool KeysNeverExceedTheDocumentedLimit(NonEmptyString id)
    {
        if (!IsSafeSegment(id.Get))
        {
            return true;
        }

        try
        {
            return CacheKey.For<CacheKeyBuilderProperties>(id.Get).Build().Length <= CacheKeyBuilder.MaximumLength;
        }
        catch (ArgumentException)
        {
            // Over-limit keys must fail loudly rather than be silently truncated.
            return true;
        }
    }

    [Property(MaxTest = 300)]
    public bool Fingerprints_AreDeterministicAndFixedWidth(string? value)
    {
        var text = value ?? string.Empty;
        return KeyFingerprint.Compute(text) == KeyFingerprint.Compute(text)
            && KeyFingerprint.Compute(text).Length == 16;
    }

    [Property(MaxTest = 300)]
    public bool DistinctKeys_DoNotCollide(NonEmptyString left, NonEmptyString right)
    {
        if (string.Equals(left.Get, right.Get, StringComparison.Ordinal))
        {
            return true;
        }

        return KeyFingerprint.Compute(left.Get) != KeyFingerprint.Compute(right.Get);
    }
}
