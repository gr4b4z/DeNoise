using System.Text.Json.Nodes;
using AlertHub.Application.Suppressions;

namespace AlertHub.Application.Tests.Suppressions;

/// <summary>Spec §22 <i>Maintenance crosses DST</i>: a window declared as wall-clock time keeps its wall-clock meaning on both transition nights (Europe/Warsaw).</summary>
[Trait("Category", "Unit")]
public sealed class WallClockTests
{
    private static readonly TimeZoneInfo Warsaw = WallClock.Zone("Europe/Warsaw");

    [Fact]
    public void Spring_forward_night_01_30_to_03_30_is_one_hour()
    {
        var starts = WallClock.ToInstant(new DateTime(2026, 3, 29, 1, 30, 0), Warsaw);
        var ends = WallClock.ToInstant(new DateTime(2026, 3, 29, 3, 30, 0), Warsaw);
        starts.Should().Be(new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero), "01:30 CET");
        ends.Should().Be(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), "03:30 CEST — the clocks skipped 02:00–03:00");
        (ends - starts).Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Autumn_night_01_30_to_03_30_is_three_hours()
    {
        var starts = WallClock.ToInstant(new DateTime(2026, 10, 25, 1, 30, 0), Warsaw);
        var ends = WallClock.ToInstant(new DateTime(2026, 10, 25, 3, 30, 0), Warsaw);
        starts.Should().Be(new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero), "01:30 CEST");
        ends.Should().Be(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.Zero), "03:30 CET — 02:00–03:00 happened twice");
        (ends - starts).Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void Non_existent_and_ambiguous_times_resolve_deterministically()
    {
        WallClock.ToInstant(new DateTime(2026, 3, 29, 2, 30, 0), Warsaw).Should().Be(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), "02:30 does not exist: it runs one hour later, at 03:30 CEST");
        WallClock.ToInstant(new DateTime(2026, 10, 25, 2, 30, 0), Warsaw).Should().Be(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), "ambiguous 02:30: first occurrence (CEST)");
        WallClock.ToLocal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.Zero), Warsaw).Should().Be(new DateTime(2026, 10, 25, 3, 30, 0));
        var act = () => WallClock.Zone("Mars/Olympus");
        act.Should().Throw<AlertHub.Application.Mapping.MappingValidationException>();
    }

    [Fact]
    public void Predicates_render_as_sentences()
    {
        var predicate = SuppressionService.ParseScope(JsonNode.Parse("""{ "all": [ { "eq": ["service", "orders"] }, { "in": ["environment", ["production", "staging"]] }, { "not": { "exists": "labels.canary" } } ] }""")!);
        PredicateText.Describe(predicate).Should().Be("service is 'orders' and environment is one of 'production', 'staging' and not (labels.canary is set)");
        var act = () => SuppressionService.ParseScope(JsonNode.Parse("""{ "eq": ["$.raw", "x"] }""")!);
        act.Should().Throw<AlertHub.Application.Mapping.MappingValidationException>("raw JSONPath references are not allowed in scopes");
    }
}
