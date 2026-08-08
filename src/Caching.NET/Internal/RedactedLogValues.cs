using System.Text;

namespace Caching.NET.Internal;

/// <summary>
/// A view over a structured log state that replaces the raw cache key with its fingerprint, and
/// re-renders the message from the redacted values.
/// </summary>
/// <remarks>
/// <para>
/// The cache engine writes its per-operation log lines with the physical cache key in a structured
/// <c>CacheKey</c> property, which is then interpolated into the rendered message. A cache key
/// routinely embeds a user id, an email address or a tenant id, so those lines put personal data
/// into whatever sink the application ships logs to — at <c>Information</c>, the level the
/// documentation recommends for the <c>Caching.NET</c> category.
/// </para>
/// <para>
/// Substituting the value here rather than dropping the line keeps the diagnostic — an operator can
/// still correlate every line about one key, and <see cref="ICacheGuard.Fingerprint"/> turns a key
/// from a support ticket into the same token — without the key itself leaving the process. This is
/// what makes <c>Security.AllowRawKeysInLogs</c> mean what its name says for engine output as well
/// as for Caching.NET's own messages.
/// </para>
/// </remarks>
internal sealed class RedactedLogValues : IReadOnlyList<KeyValuePair<string, object?>>
{
    /// <summary>The structured property the engine puts the physical cache key in.</summary>
    internal const string CacheKeyProperty = "CacheKey";

    private const string OriginalFormatProperty = "{OriginalFormat}";

    private readonly IReadOnlyList<KeyValuePair<string, object?>> _inner;
    private readonly int _keyIndex;
    private readonly string _fingerprint;

    private RedactedLogValues(IReadOnlyList<KeyValuePair<string, object?>> inner, int keyIndex, string fingerprint)
    {
        _inner = inner;
        _keyIndex = keyIndex;
        _fingerprint = fingerprint;
    }

    /// <summary>
    /// Returns a redacting view when <paramref name="state"/> carries a non-empty string
    /// <c>CacheKey</c>, otherwise <c>null</c> so the caller passes the original state through
    /// untouched and pays nothing.
    /// </summary>
    public static RedactedLogValues? TryCreate<TState>(TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            return null;
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (!string.Equals(values[i].Key, CacheKeyProperty, StringComparison.Ordinal))
            {
                continue;
            }

            return values[i].Value is string key && key.Length > 0
                ? new RedactedLogValues(values, i, KeyFingerprint.Compute(key))
                : null;
        }

        return null;
    }

    public int Count => _inner.Count;

    public KeyValuePair<string, object?> this[int index]
        => index == _keyIndex
            ? new KeyValuePair<string, object?>(CacheKeyProperty, _fingerprint)
            : _inner[index];

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Renders <c>{OriginalFormat}</c> against the redacted values. Falls back to the format string
    /// itself if the state carries no template, which cannot happen for engine output but keeps the
    /// method total.
    /// </summary>
    public override string ToString()
    {
        string? template = null;
        for (var i = 0; i < _inner.Count; i++)
        {
            if (string.Equals(_inner[i].Key, OriginalFormatProperty, StringComparison.Ordinal))
            {
                template = _inner[i].Value as string;
                break;
            }
        }

        return template is null ? _fingerprint : Render(template);
    }

    // A minimal message-template renderer: the engine's templates use plain {Name} holes with no
    // alignment or format specifiers, and '{{' / '}}' escapes. Anything it cannot resolve is left
    // as written rather than guessed at, so an unexpected template degrades to a readable line
    // instead of an exception on a logging path.
    private string Render(string template)
    {
        var builder = new StringBuilder(template.Length + 32);

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];

            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    builder.Append('{');
                    i++;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    builder.Append(template.AsSpan(i));
                    break;
                }

                var name = template.AsSpan(i + 1, close - i - 1);
                builder.Append(Resolve(name));
                i = close;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                builder.Append('}');
                i++;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private string Resolve(ReadOnlySpan<char> name)
    {
        for (var i = 0; i < _inner.Count; i++)
        {
            if (!name.Equals(_inner[i].Key, StringComparison.Ordinal))
            {
                continue;
            }

            return i == _keyIndex ? _fingerprint : _inner[i].Value?.ToString() ?? string.Empty;
        }

        return string.Concat("{", name, "}");
    }
}
