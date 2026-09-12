using DeNoise.Application.Heartbeats;
using DeNoise.Domain.Heartbeats;

namespace DeNoise.Application.Tests.Heartbeats;

[Trait("Category", "Unit")]
public sealed class HeartbeatMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static Heartbeat Interval(int minutes = 5, int grace = 2, int recovery = 1) => new()
    {
        HeartbeatId = Guid.NewGuid(),
        Name = "nightly",
        AccessScope = "s",
        ScheduleKind = ScheduleKinds.Interval,
        Interval = TimeSpan.FromMinutes(minutes),
        Grace = TimeSpan.FromMinutes(grace),
        SeverityOnMiss = "high",
        KeyId = "k",
        TokenHash = "h",
        RecoverySuccessesRequired = recovery,
        State = HeartbeatStates.Healthy,
        ExpectedNext = T0.AddMinutes(minutes),
    };

    [Fact]
    public void Late_is_informational_and_missed_needs_the_grace_to_pass()
    {
        var hb = Interval();
        HeartbeatMachine.OnTick(hb, T0.AddMinutes(5)).Changed.Should().BeFalse("exactly on time");
        var late = HeartbeatMachine.OnTick(hb, T0.AddMinutes(6));
        late.To.Should().Be(HeartbeatStates.Late);
        late.Missed.Should().BeFalse();
        HeartbeatMachine.OnTick(hb, T0.AddMinutes(7)).Changed.Should().BeFalse("within grace");
        var missed = HeartbeatMachine.OnTick(hb, T0.AddMinutes(7).AddSeconds(1));
        missed.Missed.Should().BeTrue();
        HeartbeatMachine.OnTick(hb, T0.AddMinutes(20)).Changed.Should().BeFalse("still one miss, however many intervals pass");
    }

    [Fact]
    public void A_ping_recovers_and_recomputes_expected_next()
    {
        var hb = Interval();
        HeartbeatMachine.OnTick(hb, T0.AddMinutes(8));
        var recovered = HeartbeatMachine.OnPing(hb, RunKinds.Success, null, T0.AddMinutes(9));
        recovered.Recovered.Should().BeTrue();
        hb.ExpectedNext.Should().Be(T0.AddMinutes(14));
        hb.LastPingAt.Should().Be(T0.AddMinutes(9));
    }

    [Fact]
    public void Recovery_can_require_several_pings()
    {
        var hb = Interval(recovery: 2);
        HeartbeatMachine.OnTick(hb, T0.AddMinutes(8));
        HeartbeatMachine.OnPing(hb, RunKinds.Success, null, T0.AddMinutes(9)).Changed.Should().BeFalse();
        HeartbeatMachine.OnPing(hb, RunKinds.Success, null, T0.AddMinutes(10)).Recovered.Should().BeTrue();
    }

    [Fact]
    public void Fail_and_non_zero_exit_miss_immediately_zero_exit_is_success()
    {
        var hb = Interval();
        HeartbeatMachine.OnPing(hb, RunKinds.Fail, null, T0.AddMinutes(1)).Missed.Should().BeTrue();
        HeartbeatMachine.OnPing(hb, RunKinds.Exit, 0, T0.AddMinutes(2)).Recovered.Should().BeTrue();
        HeartbeatMachine.OnPing(hb, RunKinds.Exit, 3, T0.AddMinutes(3)).Missed.Should().BeTrue();
    }

    [Fact]
    public void Start_tracks_duration_without_changing_state()
    {
        var hb = Interval();
        HeartbeatMachine.OnPing(hb, RunKinds.Start, null, T0.AddMinutes(1)).Changed.Should().BeFalse();
        hb.RunStartedAt.Should().Be(T0.AddMinutes(1));
        HeartbeatMachine.OnPing(hb, RunKinds.Success, null, T0.AddMinutes(1).AddSeconds(42));
        hb.LastRunDuration.Should().Be(TimeSpan.FromSeconds(42));
        hb.RunStartedAt.Should().BeNull();
    }

    [Fact]
    public void Pause_records_actor_and_reason_ignores_ticks_and_pings_and_resume_restarts_from_now()
    {
        var hb = Interval();
        var actor = Guid.NewGuid();
        var paused = HeartbeatMachine.Pause(hb, actor, "migration weekend", T0.AddMinutes(1));
        paused.Paused.Should().BeTrue();
        hb.PausedBy.Should().Be(actor);
        hb.PauseReason.Should().Be("migration weekend");
        HeartbeatMachine.OnTick(hb, T0.AddHours(5)).Changed.Should().BeFalse("paused is not evaluated");
        HeartbeatMachine.OnPing(hb, RunKinds.Success, null, T0.AddHours(5)).Changed.Should().BeFalse("pings while paused are recorded, not evaluated");
        var resumed = HeartbeatMachine.Resume(hb, T0.AddHours(6));
        resumed.To.Should().Be(HeartbeatStates.Unknown);
        hb.ExpectedNext.Should().Be(T0.AddHours(6).AddMinutes(5), "no false miss for the pause");
        hb.PausedBy.Should().BeNull();
    }
}
