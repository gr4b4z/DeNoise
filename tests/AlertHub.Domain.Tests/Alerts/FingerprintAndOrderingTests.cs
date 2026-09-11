using System.Text;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Time.Testing;

namespace AlertHub.Domain.Tests.Alerts;

[Trait("Category", "Unit")]
public sealed class FingerprintAndOrderingTests
{
    private static readonly Guid Integration = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Fingerprint_canonicalises_nfc_trim_and_dimension_key_order()
    {
        var a = Fingerprint.Compute(Integration, 1,
        [
            new IdentityInput("env", " prod ", null),
            new IdentityInput("res", "Café", null),
            new IdentityInput("dims", null, new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" }),
        ]);
        var b = Fingerprint.Compute(Integration, 1,
        [
            new IdentityInput("env", "prod", null),
            new IdentityInput("res", "Café", null),
            new IdentityInput("dims", null, new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }),
        ]);
        a.Canonical.Should().Be(b.Canonical);
        a.Hex.Should().Be(b.Hex);
        a.Components.Should().Equal(new IdentityComponent("env", "prod"), new IdentityComponent("res", "Café"), new IdentityComponent("dims", "a=1;b=2"));
        a.Canonical.Should().StartWith(Integration.ToString("D"));
    }

    [Fact]
    public void Fingerprint_is_case_sensitive_unless_asked_and_changes_with_identity_version_and_integration()
    {
        var upper = Fingerprint.Compute(Integration, 1, [new IdentityInput("r", "ABC", null)]);
        var lower = Fingerprint.Compute(Integration, 1, [new IdentityInput("r", "abc", null)]);
        upper.Hex.Should().NotBe(lower.Hex);
        Fingerprint.Compute(Integration, 1, [new IdentityInput("r", "ABC", null, CaseInsensitive: true)]).Hex
            .Should().Be(Fingerprint.Compute(Integration, 1, [new IdentityInput("r", "abc", null, CaseInsensitive: true)]).Hex);
        Fingerprint.Compute(Integration, 2, [new IdentityInput("r", "abc", null)]).Hex.Should().NotBe(lower.Hex);
        Fingerprint.Compute(Guid.NewGuid(), 1, [new IdentityInput("r", "abc", null)]).Hex.Should().NotBe(lower.Hex);
    }

    [Fact]
    public void Missing_component_throws_unless_optional()
    {
        var act = () => Fingerprint.Compute(Integration, 1, [new IdentityInput("r", "x", null), new IdentityInput("dims", null, null)]);
        act.Should().Throw<MissingIdentityComponentException>().Which.Component.Should().Be("dims");
        var ok = Fingerprint.Compute(Integration, 1, [new IdentityInput("r", "x", null), new IdentityInput("dims", null, null, Optional: true)]);
        ok.Components[1].Value.Should().Be(Fingerprint.EmptyMarker);
    }

    [Property(MaxTest = 200)]
    public bool Fingerprint_is_independent_of_component_dictionary_order(NonEmptyArray<NonEmptyString> keys, PositiveInt seed)
    {
        var distinct = keys.Get.Select(k => k.Get).Distinct(StringComparer.Ordinal).ToList();
        var dict = distinct.ToDictionary(k => k, k => k + "-v", StringComparer.Ordinal);
        var shuffled = distinct.OrderBy(_ => new Random(seed.Get).Next()).ToDictionary(k => k, k => k + "-v", StringComparer.Ordinal);
        var a = Fingerprint.Compute(Integration, 1, [new IdentityInput("d", null, dict)]);
        var b = Fingerprint.Compute(Integration, 1, [new IdentityInput("d", null, shuffled)]);
        return a.Hex == b.Hex;
    }

    [Property(MaxTest = 200)]
    public bool Any_changed_component_changes_the_fingerprint(NonEmptyString a, NonEmptyString b)
    {
        var va = Fingerprint.CanonicaliseString(a.Get, false);
        var vb = Fingerprint.CanonicaliseString(b.Get, false);
        if (va == vb) return true;
        var fa = Fingerprint.Compute(Integration, 1, [new IdentityInput("x", a.Get, null)]);
        var fb = Fingerprint.Compute(Integration, 1, [new IdentityInput("x", b.Get, null)]);
        return fa.Hex != fb.Hex;
    }

    [Fact]
    public void Canonical_json_sorts_keys_drops_ignored_paths_and_keeps_number_text()
    {
        var canonical = Encoding.UTF8.GetString(CanonicalJson.Canonicalise("""{ "b": [1, 2.50, {"z":1,"y":null}], "a": "x", "ts": "now" }"""u8, ["$.ts", "/b/2/z"]));
        canonical.Should().Be("""{"a":"x","b":[1,2.50,{"y":null}]}""");
    }

    [Theory]
    [InlineData("2", "1", 1)]
    [InlineData("10", "9", 1)]
    [InlineData("9", "10", -1)]
    [InlineData("b", "a", 1)]
    [InlineData("1", null, 1)]
    [InlineData("3", "3", 0)]
    public void Versions_compare_numerically_when_possible(string candidate, string? applied, int expectedSign)
    {
        Math.Sign(Ordering.CompareVersions(candidate, applied)).Should().Be(expectedSign);
    }

    [Fact]
    public void Ordering_uses_versions_when_present_else_occurred_at_with_skew_and_rejects_old_recoveries()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var t0 = time.GetUtcNow();
        var opening = Event(EventTypes.Firing, t0, version: "5");
        var episode = Episode.Open(opening, "scope", t0, time);

        Ordering.IsEffective(episode, Event(EventTypes.Update, t0.AddMinutes(1), version: "5")).Should().BeFalse("same version");
        Ordering.IsEffective(episode, Event(EventTypes.Update, t0.AddMinutes(-10), version: "6")).Should().BeTrue("newer version wins regardless of time");

        var unversioned = Episode.Open(Event(EventTypes.Firing, t0), "scope", t0, time);
        Ordering.IsEffective(unversioned, Event(EventTypes.Update, t0.AddSeconds(-60))).Should().BeTrue("within skew");
        Ordering.IsEffective(unversioned, Event(EventTypes.Update, t0.AddSeconds(-121))).Should().BeFalse("beyond skew");
        Ordering.IsEffective(unversioned, Event(EventTypes.Resolved, t0.AddSeconds(-1))).Should().BeFalse("a recovery older than the newest firing is late");
        Ordering.IsEffective(unversioned, Event(EventTypes.Resolved, t0.AddSeconds(1))).Should().BeTrue();
    }

    [Fact]
    public void Episode_state_machine_basics()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var t0 = time.GetUtcNow();
        var episode = Episode.Open(Event(EventTypes.Firing, t0, Severity.Medium), "scope", t0, time);
        episode.ConditionState.Should().Be(ConditionState.Firing);
        episode.HandlingState.Should().Be(HandlingState.New);
        episode.Version.Should().Be(1);
        episode.IsActionable.Should().BeTrue();

        var up = episode.ApplySignal(Event(EventTypes.Update, t0.AddMinutes(1), Severity.Critical), t0.AddMinutes(1));
        up.IsMaterialSeverityIncrease.Should().BeTrue();
        episode.Severity.Should().Be(Severity.Critical);
        episode.OccurrenceCount.Should().Be(2);
        episode.Version.Should().Be(2);

        episode.ApplySignal(Event(EventTypes.Update, t0.AddMinutes(2), Severity.Low), t0.AddMinutes(2));
        episode.Severity.Should().Be(Severity.Critical, "severity is the max over the episode");

        var user = Guid.NewGuid();
        episode.Acknowledge(user, t0.AddMinutes(3));
        episode.HandlingState.Should().Be(HandlingState.Acknowledged);
        episode.AssigneeId.Should().Be(user);

        var resolved = episode.ApplySourceResolved(Event(EventTypes.Resolved, t0.AddMinutes(5)), t0.AddMinutes(5));
        resolved.Kind.Should().Be(EpisodeTransitionKind.Resolved);
        episode.ConditionState.Should().Be(ConditionState.Resolved);
        episode.HandlingState.Should().Be(HandlingState.Closed);
        episode.ClosureReason.Should().Be(ClosureReason.SourceResolved);
        episode.ResolutionEvidence.Should().Be(Evidence.Source);

        var act = () => episode.ApplySignal(Event(EventTypes.Firing, t0.AddMinutes(6)), t0.AddMinutes(6));
        act.Should().Throw<InvalidEpisodeTransitionException>("closed episodes are immutable");
        var restore = () => episode.Restore(t0.AddMinutes(7));
        restore.Should().Throw<InvalidEpisodeTransitionException>("only manual_close and expired_unverified can be restored");
    }

    [Fact]
    public void Manual_close_keeps_the_last_known_condition_and_can_be_restored()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var t0 = time.GetUtcNow();
        var episode = Episode.Open(Event(EventTypes.Firing, t0), "scope", t0, time);
        episode.CloseManually("known issue", t0.AddMinutes(1));
        episode.ConditionState.Should().Be(ConditionState.Firing);
        episode.HandlingState.Should().Be(HandlingState.Closed);
        episode.ResolutionEvidence.Should().Be(Evidence.Human);
        episode.Restore(t0.AddMinutes(2));
        episode.HandlingState.Should().Be(HandlingState.Acknowledged);
        episode.RestoredFromReason.Should().Be(ClosureReason.ManualClose);
        episode.ClosureReason.Should().BeNull();
    }

    private static NormalisedEvent Event(string type, DateTimeOffset occurredAt, Severity severity = Severity.High, string? version = null) => new()
    {
        EventId = Guid.NewGuid(),
        IntegrationId = Integration,
        ReceivedAt = occurredAt,
        RawReceivedAt = occurredAt,
        EventType = type,
        OccurredAt = occurredAt,
        Severity = severity,
        SourceVersion = version,
        SourceAlertId = "a1",
        DeliveryKey = Guid.NewGuid().ToString(),
        IdentityConfidence = DeliveryKey.ConfidenceExact,
        Fingerprint = new string('a', 64),
        Summary = "s",
    };
}
