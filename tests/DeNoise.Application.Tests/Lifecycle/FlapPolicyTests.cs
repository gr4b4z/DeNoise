using DeNoise.Application.Lifecycle;
using DeNoise.Application.Mapping;
using DeNoise.Domain.Episodes;

namespace DeNoise.Application.Tests.Lifecycle;

/// <summary>The <c>flap</c> block of the lifecycle policy (spec §11.4, §12.6): defaults off, bounded, and never able to rejoin a human closure.</summary>
[Trait("Category", "Unit")]
public sealed class FlapPolicyTests
{
    [Fact]
    public void Flap_is_off_unless_configured()
    {
        var doc = LifecyclePolicyDocument.ParseYaml("lifecycle: { profile: explicit_recovery }", 1);
        doc.Flap.Enabled.Should().BeFalse("a firing after closure is recurrence (§11.3) until an operator opens a reopen window");
        doc.Flap.ReopenWindow.Should().Be(TimeSpan.Zero);
        doc.Flap.CloseHold.Should().Be(TimeSpan.Zero, "no closure notification is delayed by default (§12.6)");
        doc.Flap.NotifyOnReopen.Should().Be(NotifyOnReopen.First);
        doc.Flap.Threshold.Should().Be(3);
        LifecyclePolicyDocument.Preset(LifecycleProfiles.ExplicitRecovery).Flap.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Flap_block_parses_under_lifecycle_or_at_the_top_level()
    {
        var nested = LifecyclePolicyDocument.ParseYaml("""
            lifecycle:
              profile: explicit_recovery
              flap:
                reopen_window: 10m
                rejoin_closures: [source_resolved, inactivity_timeout]
                notify_on_reopen: every
                threshold: 2
                close_hold: 5m
                hold_max: 30m
            """, 1);
        nested.Flap.Enabled.Should().BeTrue();
        nested.Flap.ReopenWindow.Should().Be(TimeSpan.FromMinutes(10));
        nested.Flap.RejoinClosures.Should().Equal(ClosureReason.SourceResolved, ClosureReason.InactivityTimeout);
        nested.Flap.NotifyOnReopen.Should().Be(NotifyOnReopen.Every);
        nested.Flap.Threshold.Should().Be(2);
        nested.Flap.CloseHold.Should().Be(TimeSpan.FromMinutes(5));
        nested.Flap.HoldMax.Should().Be(TimeSpan.FromMinutes(30));

        var top = LifecyclePolicyDocument.ParseYaml("lifecycle: { profile: unknown }\nflap: { reopen_window: 2m }", 1);
        top.Flap.ReopenWindow.Should().Be(TimeSpan.FromMinutes(2));
        top.Flap.RejoinClosures.Should().Equal(Episode.ReopenableClosures, "unspecified means every reopenable closure");
    }

    [Fact]
    public void Human_and_administrative_closures_can_never_be_listed_as_rejoinable()
    {
        var act = () => LifecyclePolicyDocument.ParseYaml("lifecycle: { profile: unknown }\nflap: { reopen_window: 5m, rejoin_closures: [manual_close, expired_unverified, source_resolved] }", 1);
        var errors = act.Should().Throw<MappingValidationException>().Which.Errors;
        errors.Should().HaveCount(2).And.OnlyContain(e => e.Path == "$.flap.rejoin_closures");
        errors.Select(e => e.Message).Should().Contain(m => m.Contains("manual_close", StringComparison.Ordinal)).And.Contain(m => m.Contains("expired_unverified", StringComparison.Ordinal));
    }

    [Fact]
    public void Hold_and_window_are_bounded_and_reported_with_paths()
    {
        var act = () => LifecyclePolicyDocument.ParseYaml("lifecycle: { profile: unknown }\nflap: { reopen_window: 48h, close_hold: 10m, hold_max: 5m, notify_on_reopen: sometimes, threshold: 0 }", 1);
        var paths = act.Should().Throw<MappingValidationException>().Which.Errors.Select(e => e.Path).ToList();
        paths.Should().Contain("$.flap.reopen_window").And.Contain("$.flap.hold_max").And.Contain("$.flap.notify_on_reopen").And.Contain("$.flap.threshold");
    }
}
