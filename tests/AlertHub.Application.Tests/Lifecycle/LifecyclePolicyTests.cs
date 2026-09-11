using AlertHub.Application.Lifecycle;
using AlertHub.Application.Mapping;
using AlertHub.Domain.Common;

namespace AlertHub.Application.Tests.Lifecycle;

[Trait("Category", "Unit")]
public sealed class LifecyclePolicyTests
{
    [Fact]
    public void Repeating_profile_uses_the_inactivity_formula()
    {
        var doc = LifecyclePolicyDocument.ParseYaml("""
            lifecycle:
              profile: repeating_while_active
              expected_repeat_interval: 1m
              delivery_grace: 5m
            auto_resolve:
              enabled: true
              verify_before_close: api_if_available
              on_coverage_unknown: close_unverified
              on_coverage_degraded: suspend
              escalate_before_close_if_severity: [critical, high]
            """, 1);

        // max(3 × 1m + 5m, 15m) = 15m (spec §12.3)
        doc.InactivityTimeout.Should().Be(TimeSpan.FromMinutes(15));
        doc.AutoResolve.Enabled.Should().BeTrue();
        doc.AutoResolve.VerifyBeforeClose.Should().Be(VerifyBeforeClose.ApiIfAvailable);
        doc.AutoResolve.EscalateBeforeCloseIfSeverity.Should().Equal(Severity.Critical, Severity.High);
    }

    [Fact]
    public void Formula_grows_with_the_declared_interval()
    {
        var doc = LifecyclePolicyDocument.ParseYaml("lifecycle: { profile: repeating_while_active, expected_repeat_interval: 10m }", 1);
        doc.InactivityTimeout.Should().Be(TimeSpan.FromMinutes(35), "3 × 10m + 5m grace beats the 15m minimum");
    }

    [Fact]
    public void Explicit_after_silence_wins_over_the_formula()
    {
        var doc = LifecyclePolicyDocument.ParseYaml("lifecycle: { profile: repeating_while_active, expected_repeat_interval: 10m }\nauto_resolve: { after_silence: 20m }", 1);
        doc.InactivityTimeout.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Theory]
    [InlineData(LifecycleProfiles.ExplicitRecovery, 60, true)]
    [InlineData(LifecycleProfiles.QueryableState, 30, true)]
    [InlineData(LifecycleProfiles.Unknown, 60, true)]
    [InlineData(LifecycleProfiles.OneShot, 15, false)]
    public void Presets_follow_the_spec_table(string profile, int minutes, bool enabled)
    {
        var preset = LifecyclePolicyDocument.Preset(profile);
        preset.Profile.Should().Be(profile);
        preset.AutoResolve.Enabled.Should().Be(enabled);
        preset.InactivityTimeout.Should().Be(TimeSpan.FromMinutes(minutes));
    }

    [Fact]
    public void Expiry_defaults_and_overrides()
    {
        var preset = LifecyclePolicyDocument.Preset(LifecycleProfiles.Unknown);
        preset.Expiry.UnknownLifecycleReviewAfter.Should().Be(TimeSpan.FromHours(24));
        preset.Expiry.UnverifiedExpireAfter.Should().Be(TimeSpan.FromDays(7));
        preset.Expiry.NeverExpireSeverities.Should().Contain([Severity.Critical, Severity.High, Severity.Unknown]);

        var doc = LifecyclePolicyDocument.ParseYaml("""
            lifecycle: { profile: unknown }
            auto_resolve: { enabled: false }
            expiry:
              unknown_lifecycle_review_after: 1h
              unverified_expire_after: none
              never_expire_severities: [critical]
            """, 2);
        doc.AutoResolve.Enabled.Should().BeFalse();
        doc.Expiry.UnknownLifecycleReviewAfter.Should().Be(TimeSpan.FromHours(1));
        doc.Expiry.UnverifiedExpireAfter.Should().BeNull("'none' disables expiry");
        doc.Expiry.NeverExpireSeverities.Should().Equal(Severity.Critical);
    }

    [Fact]
    public void Invalid_values_are_reported_with_paths()
    {
        var act = () => LifecyclePolicyDocument.ParseYaml("""
            lifecycle: { profile: sometimes }
            auto_resolve: { verify_before_close: maybe, after_silence: soon }
            """, 1);
        var ex = act.Should().Throw<MappingValidationException>().Which;
        ex.Errors.Select(e => e.Path).Should().Contain("$.lifecycle.profile").And.Contain("$.auto_resolve.verify_before_close").And.Contain("$.auto_resolve.after_silence");
    }
}
