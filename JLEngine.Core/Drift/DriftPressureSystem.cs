using JLEngine.Core.Types;

namespace JLEngine.Core.Drift;

/// <summary>
/// Port of Drift.jl. Stateless weighted-misalignment score + 4-tier bucket.
/// Faithful port: ForceGait/ForceRhythm/ReinforceGait are populated on
/// DriftResponse exactly as Julia does, even though nothing consumes them
/// yet (an unwired escalation in the current Julia source — Phase 5 decides
/// whether to wire them up).
/// </summary>
public static class DriftPressureSystem
{
    private static double ClampAlignment(double value) => Math.Max(0.0, Math.Min(1.0, value));

    public static double Calculate(DriftPressureInput signals)
    {
        var pressure = 1.0 - (
            0.30 * ClampAlignment(signals.AgentAlignmentScore) +
            0.25 * ClampAlignment(signals.BehaviorGridAlignmentScore) +
            0.20 * ClampAlignment(signals.SafetyAlignmentScore) +
            0.15 * ClampAlignment(signals.MemoryAlignmentScore) +
            0.10 * ClampAlignment(signals.ConversationalCoherenceScore));

        return Math.Clamp(pressure, 0.0, 1.0);
    }

    public static DriftResponse GetResponseAction(double pressure)
    {
        var p = Math.Clamp(pressure, 0.0, 1.0);

        if (p < 0.10) return new DriftResponse(p, "Nominal", 0.0, null, null, null, false);
        if (p < 0.50) return new DriftResponse(p, "Soft Drift", -0.05, null, null, null, true);
        if (p < 0.75)
        {
            return new DriftResponse(
                p, "Moderate Drift", -0.10, null, null,
                "FIRM: Treat this like a growing drift fluctuation; slow down and re-check alignment.",
                false);
        }

        return new DriftResponse(
            p, "Hard Drift", -0.20, "lockstep", "strict",
            "HARD_LOCK: Containment protocols engaged. This is your safety line, not a suggestion.",
            false);
    }
}
