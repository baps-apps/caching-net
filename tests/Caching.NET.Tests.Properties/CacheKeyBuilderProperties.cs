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

    [Property(MaxTest = 300)]
    public bool SegmentsContainingTheSeparator_AreAlwaysRejected(NonEmptyString id)
    {
        if (!id.Get.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            CacheKey.For<CacheKeyBuilderProperties>(id.Get).Build();
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
