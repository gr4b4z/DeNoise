using AlertHub.Domain.Heartbeats;

namespace AlertHub.Application.Heartbeats;

public sealed record HeartbeatTransition(string From, string To, bool Changed)
{
    /// <summary>Raise (or keep) the miss episode: <c>late → missed</c>, <c>/fail</c>, non-zero exit.</summary>
    public bool Missed => Changed && To == HeartbeatStates.Missed;
    /// <summary>The miss is over: enough consecutive pings after <c>missed</c>.</summary>
    public bool Recovered => Changed && From == HeartbeatStates.Missed && To == HeartbeatStates.Healthy;
    public bool Paused => Changed && To == HeartbeatStates.Paused;
}

/// <summary>The 04 §10 state machine as pure functions over the entity, so DST and pause semantics are unit-testable.</summary>
public static class HeartbeatMachine
{
    public static HeartbeatTransition OnPing(Heartbeat hb, string runKind, int? exitCode, DateTimeOffset now)
    {
        var from = hb.State;
        if (runKind == RunKinds.Start)
        {
            hb.RunStartedAt = now;
            return new HeartbeatTransition(from, from, false);
        }
        if (hb.RunStartedAt is { } started)
        {
            hb.LastRunDuration = now - started;
            hb.RunStartedAt = null;
        }
        hb.LastPingAt = now;
        var failure = runKind == RunKinds.Fail || (runKind == RunKinds.Exit && exitCode is not 0);
        if (hb.IsPaused)
        {
            // Pings while paused are recorded, never evaluated (spec §13.3.3: visibly paused).
            return new HeartbeatTransition(from, from, false);
        }
        if (failure)
        {
            hb.ConsecutiveSuccesses = 0;
            hb.ExpectedNext = HeartbeatSchedule.NextAfter(hb, now);
            return Transition(hb, from, HeartbeatStates.Missed, now);
        }
        hb.ConsecutiveSuccesses++;
        hb.ExpectedNext = HeartbeatSchedule.NextAfter(hb, now);
        switch (from)
        {
            case HeartbeatStates.Unknown:
            case HeartbeatStates.Late:
                return Transition(hb, from, HeartbeatStates.Healthy, now);
            case HeartbeatStates.Missed:
                return hb.ConsecutiveSuccesses >= Math.Max(1, hb.RecoverySuccessesRequired)
                    ? Transition(hb, from, HeartbeatStates.Healthy, now)
                    : new HeartbeatTransition(from, from, false);
            default:
                return new HeartbeatTransition(from, from, false);
        }
    }

    /// <summary>Scheduler tick: <c>healthy → late</c> past <c>expected_next</c>, <c>late → missed</c> past <c>expected_next + grace</c>. Missing is detected here, never by a ping (spec §13.3.3).</summary>
    public static HeartbeatTransition OnTick(Heartbeat hb, DateTimeOffset now)
    {
        var from = hb.State;
        if (hb.ExpectedNext is not { } expected) return new HeartbeatTransition(from, from, false);
        if (from is HeartbeatStates.Healthy or HeartbeatStates.Late)
        {
            if (now > expected + hb.Grace)
            {
                hb.ConsecutiveSuccesses = 0;
                return Transition(hb, from, HeartbeatStates.Missed, now);
            }
            if (from == HeartbeatStates.Healthy && now > expected) return Transition(hb, from, HeartbeatStates.Late, now);
        }
        return new HeartbeatTransition(from, from, false);
    }

    public static HeartbeatTransition Pause(Heartbeat hb, Guid? actor, string reason, DateTimeOffset now, bool byMaintenance = false)
    {
        var from = hb.State;
        if (hb.IsPaused) return new HeartbeatTransition(from, from, false);
        hb.PausedBy = actor;
        hb.PausedAt = now;
        hb.PauseReason = reason;
        hb.PausedByMaintenance = byMaintenance;
        return Transition(hb, from, HeartbeatStates.Paused, now);
    }

    /// <summary>Resume ⇒ <c>unknown</c> with <c>expected_next</c> recomputed from now (04 §10): no false miss for the pause.</summary>
    public static HeartbeatTransition Resume(Heartbeat hb, DateTimeOffset now)
    {
        var from = hb.State;
        if (!hb.IsPaused) return new HeartbeatTransition(from, from, false);
        hb.PausedBy = null;
        hb.PausedAt = null;
        hb.PauseReason = null;
        hb.PausedByMaintenance = false;
        hb.ConsecutiveSuccesses = 0;
        hb.ExpectedNext = HeartbeatSchedule.NextAfter(hb, now);
        return Transition(hb, from, HeartbeatStates.Unknown, now);
    }

    private static HeartbeatTransition Transition(Heartbeat hb, string from, string to, DateTimeOffset now)
    {
        if (from == to) return new HeartbeatTransition(from, to, false);
        hb.State = to;
        hb.Touch(now);
        return new HeartbeatTransition(from, to, true);
    }
}
