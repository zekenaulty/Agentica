namespace Agentica.Tests;

public sealed class AgenticaIdsTests
{
    [Fact]
    public void New_preserves_readable_prefix_and_uses_complete_uuid()
    {
        const string prefix = "receipt";

        var id = AgenticaIds.New(prefix);

        Assert.StartsWith($"{prefix}_", id, StringComparison.Ordinal);

        var uuid = id[(prefix.Length + 1)..];
        Assert.Equal(32, uuid.Length);
        Assert.True(Guid.TryParseExact(uuid, "N", out _));
        Assert.Equal(uuid.ToLowerInvariant(), uuid);
    }

    [Fact]
    public void New_generates_distinct_identifiers_for_the_same_prefix()
    {
        const int sampleSize = 10_000;

        var ids = Enumerable
            .Range(0, sampleSize)
            .Select(_ => AgenticaIds.New("run"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(sampleSize, ids.Count);
    }
}
