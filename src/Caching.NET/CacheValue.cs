namespace Caching.NET;

/// <summary>
/// The result of a cache read: either a value, or the absence of one. A cached <c>null</c> is a
/// value, not an absence, which is why a nullable return type cannot express this.
/// </summary>
/// <typeparam name="TValue">The cached value type.</typeparam>
public readonly struct CacheValue<TValue> : IEquatable<CacheValue<TValue>>
{
    private readonly TValue _value;

    private CacheValue(TValue value, bool hasValue)
    {
        _value = value;
        HasValue = hasValue;
    }

    /// <summary>Whether a value was found.</summary>
    public bool HasValue { get; }

    /// <summary>The value found.</summary>
    /// <exception cref="InvalidOperationException"><see cref="HasValue"/> is <c>false</c>.</exception>
    public TValue Value => HasValue
        ? _value
        : throw new InvalidOperationException("No value is present. Check HasValue before reading Value, or call GetValueOrDefault.");

    /// <summary>An empty result.</summary>
    public static CacheValue<TValue> None => default;

    /// <summary>A result carrying <paramref name="value"/>.</summary>
    /// <param name="value">The value, which may itself be <c>null</c>.</param>
    public static CacheValue<TValue> Of(TValue value) => new(value, hasValue: true);

    /// <summary>Returns the value, or <paramref name="fallback"/> when there is none.</summary>
    /// <param name="fallback">Returned when <see cref="HasValue"/> is <c>false</c>.</param>
    public TValue? GetValueOrDefault(TValue? fallback = default) => HasValue ? _value : fallback;

    /// <summary>Splits the result into its two parts.</summary>
    /// <param name="hasValue">Receives <see cref="HasValue"/>.</param>
    /// <param name="value">Receives the value, or <c>default</c> when there is none.</param>
    public void Deconstruct(out bool hasValue, out TValue? value)
    {
        hasValue = HasValue;
        value = HasValue ? _value : default;
    }

    /// <inheritdoc />
    public bool Equals(CacheValue<TValue> other)
        => HasValue == other.HasValue
        && (!HasValue || EqualityComparer<TValue>.Default.Equals(_value, other._value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CacheValue<TValue> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HasValue ? HashCode.Combine(true, _value) : 0;

    /// <summary>Equality operator.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator ==(CacheValue<TValue> left, CacheValue<TValue> right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator !=(CacheValue<TValue> left, CacheValue<TValue> right) => !left.Equals(right);
}
