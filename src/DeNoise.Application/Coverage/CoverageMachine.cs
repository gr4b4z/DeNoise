using DeNoise.Domain.Ops;

namespace DeNoise.Application.Coverage;

/// <summary>Result of one evaluation step: the new state plus what the caller must do about it.</summary>
public sealed record CoverageStep(string From, string To, bool Changed)
{
    /// <summary>Coverage was verified and is now lost: raise the coverage episode, suspend inactivity closure, condition → unknown.</summary>
    public bool Lost => Changed && CoverageStates.IsLost(To) && !CoverageStates.IsLost(From);
    /// <summary>Coverage is back after a loss: resolve the coverage episode, resume timers, condition → firing.</summary>
    public bool Restored => Changed && To == CoverageStates.Healthy && CoverageStates.IsLost(From);
    /// <summary>degraded → unavailable: same episode, severity bump (04 §9).</summary>
    public bool Worsened => Changed && From == CoverageStates.Degraded && To == CoverageStates.Unavailable;
}

/// <summary>
/// The per-integration coverage state machine of 04 §9 as a pure function over the stored row, so the transitions are unit-testable.
/// Time-driven transitions run from the scheduler tick; signal-driven ones from the processing of heartbeat/canary events.
/// </summary>
public static class CoverageMachine
{
    /// <summary>A canary signal arrived (or an explicit failure, <paramref name="success"/> = false).</summary>
    public static CoverageStep OnSignal(CoverageState state, CanaryMethod method, DateTimeOffset now, bool success = true)
    {
        var from = state.State;
        if (!success)
        {
            state.ConsecutiveSuccesses = 0;
            state.Detail = null;
            return Transition(state, from, CoverageStates.IsLost(from) ? from : CoverageStates.Degraded, now);
        }
        state.LastSignalAt = now;
        state.ConsecutiveSuccesses++;
        switch (from)
        {
            case CoverageStates.Unknown:
            case CoverageStates.NotConfigured:
            case CoverageStates.Delayed:
                return Transition(state, from, CoverageStates.Healthy, now);
            case CoverageStates.Degraded:
            case CoverageStates.Unavailable:
                return state.ConsecutiveSuccesses >= method.RecoverySuccessesRequired
                    ? Transition(state, from, CoverageStates.Healthy, now)
                    : new CoverageStep(from, from, false);
            default:
                return new CoverageStep(from, from, false);
        }
    }

    /// <summary>Scheduler tick: silence past the configured thresholds moves healthy → delayed → degraded → unavailable.</summary>
    public static CoverageStep OnTick(CoverageState state, CanaryMethod method, DateTimeOffset now)
    {
        var from = state.State;
        if (state.LastSignalAt is not { } last) return new CoverageStep(from, from, false); // never established: unknown stays unknown (spec §12.4)
        var silence = now - last;
        var target = from switch
        {
            CoverageStates.Healthy when silence >= method.AlertAfter => CoverageStates.Degraded,
            CoverageStates.Healthy when silence >= method.DelayedAfter => CoverageStates.Delayed,
            CoverageStates.Delayed when silence >= method.AlertAfter => CoverageStates.Degraded,
            CoverageStates.Degraded when silence >= method.UnavailableAfter => CoverageStates.Unavailable,
            _ => from,
        };
        if (target == from) return new CoverageStep(from, from, false);
        if (CoverageStates.IsLost(target)) state.ConsecutiveSuccesses = 0;
        return Transition(state, from, target, now);
    }

    private static CoverageStep Transition(CoverageState state, string from, string to, DateTimeOffset now)
    {
        if (from == to) return new CoverageStep(from, to, false);
        state.State = to;
        state.Since = now;
        return new CoverageStep(from, to, true);
    }
}
