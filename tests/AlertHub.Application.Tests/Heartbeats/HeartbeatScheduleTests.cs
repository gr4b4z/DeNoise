using AlertHub.Application.Heartbeats;
using AlertHub.Domain.Heartbeats;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace AlertHub.Application.Tests.Heartbeats;

[Trait("Category", "Unit")]
public sealed class HeartbeatScheduleTests
{
    private static Heartbeat Cron(string cron, string tz) => new() { Name = "n", AccessScope = "s", ScheduleKind = ScheduleKinds.Cron, Cron = cron, ScheduleTz = tz, SeverityOnMiss = "high", KeyId = "k", TokenHash = "h" };

    [Fact]
    public void Interval_schedules_add_the_interval_to_the_ping()
    {
        var hb = new Heartbeat { Name = "n", AccessScope = "s", ScheduleKind = ScheduleKinds.Interval, Interval = TimeSpan.FromMinutes(5), SeverityOnMiss = "high", KeyId = "k", TokenHash = "h" };
        var t = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        HeartbeatSchedule.NextAfter(hb, t).Should().Be(t.AddMinutes(5));
        HeartbeatSchedule.Describe(hb).Should().Be("every 5 min");
    }

    [Fact]
    public void Spring_forward_night_skips_the_hour_that_does_not_exist_without_an_extra_alarm()
    {
        // Europe/Warsaw 2026-03-29: 02:00 CET jumps to 03:00 CEST — 02:30 local does not exist that night.
        var hb = Cron("30 2 * * *", "Europe/Warsaw");
        var saturday = new DateTimeOffset(2026, 3, 28, 2, 30, 0, TimeSpan.FromHours(1)); // 02:30 CET on the 28th, the last normal run
        var next = HeartbeatSchedule.NextAfter(hb, saturday)!.Value;
        next.Should().Be(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)), "Cronos runs the skipped occurrence at the first valid instant (03:00 CEST), once");
        var after = HeartbeatSchedule.NextAfter(hb, next)!.Value;
        after.Should().Be(new DateTimeOffset(2026, 3, 30, 2, 30, 0, TimeSpan.FromHours(2)), "the next day is a normal 02:30");
        (after - next).Should().BeGreaterThan(TimeSpan.FromHours(20), "no second alarm on the transition night");
    }

    [Fact]
    public void Fall_back_night_runs_the_repeated_hour_once()
    {
        // Europe/Warsaw 2026-10-25: 03:00 CEST goes back to 02:00 CET — 02:30 local happens twice.
        var hb = Cron("30 2 * * *", "Europe/Warsaw");
        var saturday = new DateTimeOffset(2026, 10, 24, 2, 30, 0, TimeSpan.FromHours(2));
        var first = HeartbeatSchedule.NextAfter(hb, saturday)!.Value;
        first.UtcDateTime.Should().Be(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), "02:30 CEST, the first occurrence");
        var second = HeartbeatSchedule.NextAfter(hb, first)!.Value;
        second.Should().Be(new DateTimeOffset(2026, 10, 26, 2, 30, 0, TimeSpan.FromHours(1)), "no double alarm: the repeated 02:30 CET is not a second occurrence");
    }

    [Fact]
    public void Preview_lists_the_next_runs_in_order()
    {
        var runs = HeartbeatSchedule.Preview(ScheduleKinds.Cron, null, "0 */6 * * *", "UTC", new DateTimeOffset(2026, 9, 11, 1, 0, 0, TimeSpan.Zero), 3);
        runs.Should().HaveCount(3);
        runs.Should().BeInAscendingOrder();
        runs[0].Should().Be(new DateTimeOffset(2026, 9, 11, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Validation_requires_a_timezone_for_cron_and_rejects_nonsense()
    {
        var errors = new List<AlertHub.Application.Mapping.MappingValidationError>();
        HeartbeatSchedule.Validate(ScheduleKinds.Cron, null, "30 2 * * *", null, TimeSpan.FromMinutes(5), errors);
        errors.Should().ContainSingle(e => e.Path == "$.schedule.timezone");
        errors.Clear();
        HeartbeatSchedule.Validate(ScheduleKinds.Cron, null, "every day at noon", "Mars/Olympus", TimeSpan.Zero, errors);
        errors.Select(e => e.Path).Should().Contain("$.schedule.cron").And.Contain("$.schedule.timezone");
        errors.Clear();
        HeartbeatSchedule.Validate(ScheduleKinds.Interval, TimeSpan.FromSeconds(5), null, null, TimeSpan.Zero, errors);
        errors.Should().ContainSingle(e => e.Path == "$.schedule.interval");
    }

    /// <summary>10 §9 property: across DST transitions in three zones, <c>expected_next</c> is strictly increasing and never repeats a wall-clock occurrence.</summary>
    [Property(MaxTest = 300)]
    public Property Expected_next_is_strictly_increasing_across_dst_transitions()
    {
        var zones = new[] { "Europe/Warsaw", "America/New_York", "Australia/Sydney" };
        var transitionDays = new[] { new DateTime(2026, 3, 28), new DateTime(2026, 10, 24), new DateTime(2026, 3, 7), new DateTime(2026, 10, 31), new DateTime(2026, 4, 4), new DateTime(2026, 10, 3) };
        var gen = from minute in Gen.Choose(0, 59)
                  from hour in Gen.Choose(0, 23)
                  from zone in Gen.Elements(zones)
                  from day in Gen.Elements(transitionDays)
                  select (minute, hour, zone, day);
        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var hb = Cron($"{t.minute} {t.hour} * * *", t.zone);
            var cursor = new DateTimeOffset(t.day, TimeSpan.Zero).AddHours(-12);
            var previous = cursor;
            for (var i = 0; i < 4; i++)
            {
                var next = HeartbeatSchedule.NextAfter(hb, previous);
                if (next is null || next <= previous) return false;
                if (i > 0 && next.Value - previous < TimeSpan.FromHours(20)) return false; // one occurrence per day, even across the transition
                previous = next.Value;
            }
            return true;
        });
    }
}
