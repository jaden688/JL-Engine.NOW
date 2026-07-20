using JLEngine.Core.Types;

namespace JLEngine.Core.Investment;

/// <summary>
/// Port of Investment.jl. "Investment" = a 0-1 engagement/stakes score
/// (distinct from sentiment/valence) that picks which "gear" the Aperture
/// uses this turn. In Julia (and in this port's original Phase 1 pass),
/// InvestmentGear's label ("low"/"medium"/"high"/"max") was never a
/// drive-type gear_modifiers recognized, so this selection was inert.
/// Phase 5 fix: Gear.Modifiers (Types.cs) now has a second tier table for
/// exactly these four labels, so InvestmentGear's output is live again —
/// investment level now actually changes the Aperture's reaction speed.
/// </summary>
public sealed class InvestmentSystem
{
    public double Level { get; private set; } = 0.0;
    public double Target { get; private set; } = 0.5;

    public double InvestmentTarget() => Target;

    public double UpdateInvestment(TurnSignals signals, double momentum = 0.0, double driftPressure = 0.0)
    {
        var engagement = Math.Clamp(
            0.35 * signals.Arousal +
            0.25 * signals.MemoryDensity +
            0.20 * signals.Pace +
            0.20 * momentum,
            0.0, 1.0);

        var pressurePenalty = Math.Clamp(driftPressure * 0.15, 0.0, 0.35);
        var level = Math.Clamp(engagement - pressurePenalty, 0.0, 1.0);
        Level = level;
        Target = level;
        return level;
    }

    public static string InvestmentGear(double level)
    {
        var levelF = Math.Clamp(level, 0.0, 1.0);
        if (levelF < 0.25) return "low";
        if (levelF < 0.6) return "medium";
        if (levelF < 0.85) return "high";
        return "max";
    }
}
