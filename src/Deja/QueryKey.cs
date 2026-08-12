using System.Collections;
using System.Globalization;
using System.Text;

namespace Deja;

/// <summary>
/// A structured, ordered cache key. Segment order matters — <c>Of("todos", 1)</c> and
/// <c>Of(1, "todos")</c> are different keys — while dictionary segments are normalized by sorting
/// their keys, so property order in an ad-hoc filter object does not change identity.
/// </summary>
/// <remarks>
/// <para>
/// Supported segment types: <see cref="string"/>, all numeric primitives, <see cref="bool"/>,
/// <see cref="char"/>, <see cref="Guid"/>, <see cref="DateTime"/>, <see cref="DateTimeOffset"/>,
/// <see cref="DateOnly"/>, <see cref="TimeOnly"/>, <see cref="TimeSpan"/>, enums,
/// <see langword="null"/>, <see cref="IEnumerable"/> of the above, string-keyed
/// <see cref="IDictionary"/> instances, and any type implementing <see cref="IQueryKeySegment"/>.
/// An unsupported segment throws at construction — failing loudly at the call site beats a
/// silently colliding cache entry.
/// </para>
/// <para>
/// The canonical form is computed once, by hand rather than with a JSON serializer, to keep the
/// package dependency-free and trimming/AOT-safe: strings are quoted and escaped (so
/// <c>["a,b"]</c> and <c>["a","b"]</c> cannot collide), numbers and dates use
/// <see cref="CultureInfo.InvariantCulture"/>, and dictionaries are written with keys in ordinal
/// order.
/// </para>
/// </remarks>
public sealed class QueryKey : IEquatable<QueryKey>
{
    // Guards against cyclic segment graphs (a list containing itself) blowing the stack.
    private const int MaxDepth = 32;

    private readonly object?[] _segments;
    private readonly string[] _canonicalSegments;

    private QueryKey(object?[] segments)
    {
        _segments = segments;
        _canonicalSegments = new string[segments.Length];

        for (var i = 0; i < segments.Length; i++)
        {
            var builder = new StringBuilder();
            WriteSegment(builder, segments[i], depth: 0);
            _canonicalSegments[i] = builder.ToString();
        }

        Hash = $"[{string.Join(",", _canonicalSegments)}]";
    }

    /// <summary>
    /// Creates a key from ordered segments: <c>QueryKey.Of("todos", "detail", 5)</c>.
    /// </summary>
    /// <param name="segments">The ordered segments; see the class remarks for supported types.</param>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// No segments were given, or a segment is of an unsupported type — the message names the type
    /// and the escape hatches (<see cref="IQueryKeySegment"/> or a string-keyed dictionary).
    /// </exception>
    public static QueryKey Of(params object?[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Length == 0)
        {
            throw new ArgumentException("A QueryKey needs at least one segment.", nameof(segments));
        }

        // Cloned so a caller mutating the array afterwards cannot desynchronize Segments from the
        // canonical form computed from it.
        return new QueryKey((object?[])segments.Clone());
    }

    /// <summary>
    /// The named equivalent of the implicit <see cref="string"/> conversion. Whitespace-only and
    /// <see langword="null"/> input yields <see langword="null"/> (no key), mirroring how the
    /// pre-cache string key treated whitespace as "no key".
    /// </summary>
    public static QueryKey? FromString(string? key)
        => string.IsNullOrWhiteSpace(key) ? null : Of(key);

    /// <summary>
    /// Keeps every existing <c>QueryKey = "todos"</c> call site compiling and meaning
    /// <c>QueryKey.Of("todos")</c>. Null or whitespace converts to <see langword="null"/> — no key.
    /// </summary>
    public static implicit operator QueryKey?(string? key) => FromString(key);

    /// <summary>
    /// The canonical string form of the whole key: stable, deterministic, and unique per distinct
    /// key. Used as the registry dictionary key.
    /// </summary>
    internal string Hash { get; }

    /// <summary>The original segments, in order.</summary>
    internal IReadOnlyList<object?> Segments => _segments;

    /// <summary>Number of segments; per-prefix defaults are ordered by this.</summary>
    internal int SegmentCount => _segments.Length;

    /// <summary>
    /// True when this key's leading segments equal every segment of <paramref name="prefix"/> —
    /// <c>["todos", 1]</c> starts with <c>["todos"]</c>. Compared segment-by-segment on canonical
    /// forms, never as a string prefix of the whole hash, so <c>["todo"]</c> does not match
    /// <c>["todos"]</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="prefix"/> is <see langword="null"/>.</exception>
    public bool StartsWith(QueryKey prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (prefix._canonicalSegments.Length > _canonicalSegments.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix._canonicalSegments.Length; i++)
        {
            if (!string.Equals(_canonicalSegments[i], prefix._canonicalSegments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool Equals(QueryKey? other)
        => other is not null && string.Equals(Hash, other.Hash, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as QueryKey);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Hash);

    /// <summary>Value equality on the canonical form.</summary>
    public static bool operator ==(QueryKey? left, QueryKey? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>Value inequality on the canonical form.</summary>
    public static bool operator !=(QueryKey? left, QueryKey? right) => !(left == right);

    /// <summary>The canonical form, e.g. <c>["todos",{"page":2}]</c>.</summary>
    public override string ToString() => Hash;

    private static void WriteSegment(StringBuilder builder, object? value, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ArgumentException(
                $"QueryKey segments nest deeper than {MaxDepth} levels — most likely a cyclic collection.");
        }

        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string s:
                WriteQuoted(builder, s);
                break;
            case bool b:
                builder.Append(b ? "true" : "false");
                break;
            case char c:
                WriteQuoted(builder, c.ToString());
                break;
            // Before the collection cases, so a custom type that also happens to be enumerable
            // uses the representation its author chose.
            case IQueryKeySegment segment:
                WriteQuoted(
                    builder,
                    segment.ToKeySegment() ?? throw new ArgumentException(
                        $"{segment.GetType().Name}.ToKeySegment() returned null; it must return a stable string."));
                break;
            // The numeric value rather than the name: an enum member can be renamed without anyone
            // thinking about cached data, but its value cannot change silently.
            case Enum e:
                builder.Append(e.ToString("D"));
                break;
            case Guid g:
                builder.Append(g.ToString("D"));
                break;
            case DateTime dateTime:
                builder.Append(dateTime.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dateTimeOffset:
                builder.Append(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateOnly dateOnly:
                builder.Append(dateOnly.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeOnly timeOnly:
                builder.Append(timeOnly.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan timeSpan:
                builder.Append(timeSpan.ToString("c", CultureInfo.InvariantCulture));
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                builder.Append(((IFormattable)value).ToString(format: null, CultureInfo.InvariantCulture));
                break;
            // Before IEnumerable: every dictionary is also enumerable, and the object form (sorted
            // keys) is what makes property order irrelevant.
            case IDictionary dictionary:
                WriteObject(builder, dictionary, depth);
                break;
            case IEnumerable enumerable:
                WriteArray(builder, enumerable, depth);
                break;
            default:
                throw new ArgumentException(
                    $"QueryKey does not support segments of type '{value.GetType()}'. Supported: " +
                    "string, numeric primitives, bool, char, Guid, DateTime, DateTimeOffset, " +
                    "DateOnly, TimeOnly, TimeSpan, enums, null, collections of these, and " +
                    "string-keyed dictionaries. For anonymous types or records, use a " +
                    "Dictionary<string, object?> or implement IQueryKeySegment on a named type.");
        }
    }

    private static void WriteObject(StringBuilder builder, IDictionary dictionary, int depth)
    {
        var entries = new List<(string Key, object? Value)>(dictionary.Count);

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
            {
                throw new ArgumentException(
                    $"QueryKey dictionary segments need string keys; found a key of type " +
                    $"'{entry.Key?.GetType().ToString() ?? "null"}'.");
            }

            entries.Add((key, entry.Value));
        }

        // Ordinal sort is what makes { Page, Status } and { Status, Page } the same key.
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        builder.Append('{');

        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            WriteQuoted(builder, entries[i].Key);
            builder.Append(':');
            WriteSegment(builder, entries[i].Value, depth + 1);
        }

        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, IEnumerable enumerable, int depth)
    {
        builder.Append('[');

        var first = true;

        foreach (var item in enumerable)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            WriteSegment(builder, item, depth + 1);
        }

        builder.Append(']');
    }

    // Quoting plus escaping gives injectivity: no string content can fake a delimiter, so
    // ["a,b"] and ["a","b"] stay distinct.
    private static void WriteQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');

        foreach (var ch in value)
        {
            if (ch is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        builder.Append('"');
    }
}

/// <summary>
/// Lets a custom type participate as a <see cref="QueryKey"/> segment. Implement it on the types
/// you pass to <see cref="QueryKey.Of"/> that are not among the built-in supported segment types
/// (strings, primitives, <see cref="Guid"/>, dates and times, enums, and collections or
/// string-keyed dictionaries of those).
/// </summary>
/// <remarks>
/// Deliberately an interface rather than reflection over arbitrary objects: key hashing runs on
/// every cache lookup and must stay trimming- and AOT-safe, which reflection-based property
/// reading is not. Anonymous types therefore cannot be segments — use a
/// <c>Dictionary&lt;string, object?&gt;</c> for ad-hoc shapes (property order is normalized by
/// sorting keys), or implement this interface on a named type.
/// </remarks>
public interface IQueryKeySegment
{
    /// <summary>
    /// A stable, deterministic string identifying this value. Equal values must return equal
    /// strings and distinct values distinct strings, across sessions — the string participates
    /// directly in the key's identity, exactly as a string segment with the same content would.
    /// </summary>
    string ToKeySegment();
}
