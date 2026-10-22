using DeNoise.Domain.Common;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Domain.Tests.Common;

[Trait("Category", "Unit")]
public sealed class IdsTests
{
    [Fact]
    public void Ids_are_version_7_and_time_ordered()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var first = Ids.New(time);
        time.Advance(TimeSpan.FromMilliseconds(5));
        var second = Ids.New(time);

        first.Version.Should().Be(7);
        string.CompareOrdinal(first.ToString(), second.ToString()).Should().BeNegative();
    }

    [Theory]
    [InlineData("critical", Severity.Critical, 4)]
    [InlineData("unknown", Severity.Unknown, 3)]
    [InlineData("Sev9", Severity.Unknown, 3)]
    [InlineData("informational", Severity.Informational, 0)]
    public void Severity_parses_and_ranks_unknown_as_high(string wire, Severity expected, int rank)
    {
        var parsed = SeverityExtensions.ParseWire(wire);
        parsed.Should().Be(expected);
        parsed.Rank().Should().Be(rank);
    }
}
