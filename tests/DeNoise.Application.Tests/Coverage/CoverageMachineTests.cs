using DeNoise.Application.Coverage;
using DeNoise.Domain.Ops;

namespace DeNoise.Application.Tests.Coverage;

[Trait("Category", "Unit")]
public sealed class CoverageMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly CanaryMethod Canary = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), 3);

    private static CoverageState Fresh() => new() { IntegrationId = Guid.NewGuid(), State = CoverageStates.Unknown, Since = T0 };

    [Fact]
    public void First_success_establishes_coverage()
    {
        var state = Fresh();
        var step = CoverageMachine.OnSignal(state, Canary, T0);
        step.Should().Be(new CoverageStep(CoverageStates.Unknown, CoverageStates.Healthy, true));
        state.LastSignalAt.Should().Be(T0);
    }

    [Fact]
    public void Silence_walks_healthy_delayed_degraded_unavailable_and_only_the_degraded_step_is_a_loss()
    {
        var state = Fresh();
        CoverageMachine.OnSignal(state, Canary, T0);

        CoverageMachine.OnTick(state, Canary, T0.AddMinutes(9)).Changed.Should().BeFalse();
        var delayed = CoverageMachine.OnTick(state, Canary, T0.AddMinutes(10));
        delayed.To.Should().Be(CoverageStates.Delayed);
        delayed.Lost.Should().BeFalse("delayed is below the alert threshold (spec §13.5)");

        var degraded = CoverageMachine.OnTick(state, Canary, T0.AddMinutes(15));
        degraded.To.Should().Be(CoverageStates.Degraded);
        degraded.Lost.Should().BeTrue();

        var unavailable = CoverageMachine.OnTick(state, Canary, T0.AddMinutes(30));
        unavailable.To.Should().Be(CoverageStates.Unavailable);
        unavailable.Worsened.Should().BeTrue();
        unavailable.Lost.Should().BeFalse("same outage, same episode");
    }

    [Fact]
    public void A_healthy_integration_that_skips_the_delayed_tick_still_degrades()
    {
        var state = Fresh();
        CoverageMachine.OnSignal(state, Canary, T0);
        CoverageMachine.OnTick(state, Canary, T0.AddMinutes(20)).To.Should().Be(CoverageStates.Degraded);
    }

    [Fact]
    public void Recovery_needs_consecutive_successes_and_a_failure_resets_the_count()
    {
        var state = Fresh();
        CoverageMachine.OnSignal(state, Canary, T0);
        CoverageMachine.OnTick(state, Canary, T0.AddMinutes(16)).Lost.Should().BeTrue();

        CoverageMachine.OnSignal(state, Canary, T0.AddMinutes(17)).Changed.Should().BeFalse();
        CoverageMachine.OnSignal(state, Canary, T0.AddMinutes(18)).Changed.Should().BeFalse();
        CoverageMachine.OnSignal(state, Canary, T0.AddMinutes(19), success: false).Changed.Should().BeFalse("flap: stays degraded, count resets");
        CoverageMachine.OnSignal(state, Canary, T0.AddMinutes(20)).Changed.Should().BeFalse();
        CoverageMachine.OnSignal(state, Canary, T0.AddMinutes(21)).Changed.Should().BeFalse();
        var restored = CoverageMachine.OnSignal(state, Canary, T0.AddMinutes(22));
        restored.Restored.Should().BeTrue();
        state.State.Should().Be(CoverageStates.Healthy);
    }

    [Fact]
    public void Never_established_coverage_does_not_degrade_on_silence()
    {
        var state = Fresh();
        CoverageMachine.OnTick(state, Canary, T0.AddDays(1)).Changed.Should().BeFalse("unknown is not failing (spec §12.4)");
    }

    [Fact]
    public void Explicit_failure_degrades_immediately()
    {
        var state = Fresh();
        CoverageMachine.OnSignal(state, Canary, T0);
        var step = CoverageMachine.OnSignal(state, Canary, T0.AddMinutes(1), success: false);
        step.Lost.Should().BeTrue();
    }

    [Fact]
    public void Coverage_config_parses_the_spec_yaml_shape()
    {
        var config = CoverageConfig.Parse("""
            {"coverage":{"methods":[{"type":"managed_canary","expected_interval":"5m","delayed_after":"10m","alert_after":"15m","unavailable_after":"30m","recovery_successes_required":3},{"type":"registered_heartbeat","heartbeat_id":"hb_atlas_poller"},{"type":"api_probe","interval":"5m"}]},"on_failure":{"suspend_inactivity_resolution":true}}
            """);
        config.Canary.Should().Be(Canary);
        config.HeartbeatIds.Should().Equal("hb_atlas_poller");
        config.HasMethods.Should().BeTrue();
        CoverageConfig.Parse("{}").HasMethods.Should().BeFalse();
    }
}
