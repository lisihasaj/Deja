namespace Deja.Tests;

public class QueryKeyTests
{
    private enum Status
    {
        Open = 0,
        Done = 1,
    }

    private sealed class TenantSegment(string id) : IQueryKeySegment
    {
        public string ToKeySegment() => $"tenant:{id}";
    }

    [Fact]
    public void Of_SegmentOrder_Matters()
    {
        var byStatusThenPage = QueryKey.Of("todos", "done", 2);
        var byPageThenStatus = QueryKey.Of("todos", 2, "done");

        Assert.NotEqual(byStatusThenPage, byPageThenStatus);
    }

    [Fact]
    public void Of_DictionaryPropertyOrder_DoesNotMatter()
    {
        var pageFirst = QueryKey.Of("todos", new Dictionary<string, object?> { ["Page"] = 1, ["Status"] = "done" });
        var statusFirst = QueryKey.Of("todos", new Dictionary<string, object?> { ["Status"] = "done", ["Page"] = 1 });

        Assert.Equal(pageFirst, statusFirst);
        Assert.Equal(pageFirst.GetHashCode(), statusFirst.GetHashCode());
    }

    private static readonly string[] _joinedListSegment = ["b,c"];
    private static readonly string[] _splitListSegment = ["b", "c"];

    [Fact]
    public void Of_StringsContainingDelimiters_DoNotCollide()
    {
        Assert.NotEqual(QueryKey.Of("a,b"), QueryKey.Of("a", "b"));
        Assert.NotEqual(QueryKey.Of("a", _joinedListSegment), QueryKey.Of("a", _splitListSegment));
        Assert.NotEqual(QueryKey.Of("a\",\"b"), QueryKey.Of("a", "b"));
    }

    [Fact]
    public void Of_NestedListVsFlat_DoNotCollide()
    {
        Assert.NotEqual(QueryKey.Of("a", new object[] { "b", "c" }), QueryKey.Of("a", "b", "c"));
    }

    [Fact]
    public void Of_UnsupportedSegment_ThrowsNamingTheType()
    {
        var ex = Assert.Throws<ArgumentException>(() => QueryKey.Of("todos", new object()));

        Assert.Contains("System.Object", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IQueryKeySegment", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Of_AnonymousType_ThrowsWithGuidance()
    {
        // Reflection-free by design: anonymous types are rejected, dictionaries are the escape hatch.
        var ex = Assert.Throws<ArgumentException>(() => QueryKey.Of("todos", new { Page = 1 }));

        Assert.Contains("Dictionary<string, object?>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Of_NoSegments_Throws()
    {
        Assert.Throws<ArgumentException>(() => QueryKey.Of());
    }

    [Fact]
    public void Of_CustomSegment_ParticipatesInIdentity()
    {
        var first = QueryKey.Of("todos", new TenantSegment("42"));
        var same = QueryKey.Of("todos", new TenantSegment("42"));
        var other = QueryKey.Of("todos", new TenantSegment("43"));

        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Of_PrimitiveVariety_IsStable()
    {
        var guid = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        var first = QueryKey.Of("k", guid, at, Status.Done, 1.5, true, (object?)null);
        var second = QueryKey.Of("k", guid, at, Status.Done, 1.5, true, (object?)null);

        Assert.Equal(first, second);
    }

    [Fact]
    public void StartsWith_MatchesByWholeSegments_NotByStringPrefix()
    {
        Assert.True(QueryKey.Of("todos", 1).StartsWith(QueryKey.Of("todos")));
        Assert.True(QueryKey.Of("todos").StartsWith(QueryKey.Of("todos")));
        Assert.False(QueryKey.Of("todos", 1).StartsWith(QueryKey.Of("todo")));
        Assert.False(QueryKey.Of("todos").StartsWith(QueryKey.Of("todos", 1)));
        Assert.False(QueryKey.Of("todos", 1).StartsWith(QueryKey.Of("todos", 2)));
    }

    [Fact]
    public void ImplicitConversion_FromString_EqualsOf()
    {
        QueryKey? converted = "todos";

        Assert.Equal(QueryKey.Of("todos"), converted);
    }

    [Fact]
    public void ImplicitConversion_FromNullOrWhitespace_IsNoKey()
    {
        // Mirrors the pre-cache behavior where a whitespace string key meant "no key".
        Assert.Null(QueryKey.FromString(null));
        Assert.Null(QueryKey.FromString("  "));
    }

    [Fact]
    public void EqualityOperators_UseValueEquality()
    {
        var left = QueryKey.Of("todos", 1);
        var right = QueryKey.Of("todos", 1);

        Assert.True(left == right);
        Assert.False(left != right);
        Assert.False(left == null);
        Assert.True((QueryKey?)null == (QueryKey?)null);
    }

    [Fact]
    public void Of_EnumSegment_HashesByUnderlyingValue()
    {
        // Documented collision: an enum hashes as its numeric value, like JSON serialization would.
        Assert.Equal(QueryKey.Of("k", Status.Done), QueryKey.Of("k", 1));
    }
}
