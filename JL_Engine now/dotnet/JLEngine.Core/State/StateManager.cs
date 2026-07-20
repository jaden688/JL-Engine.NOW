using JLEngine.Core.Types;

namespace JLEngine.Core.State;

/// <summary>
/// Port of State.jl. A smoothed, decaying "mood" tracker for the engine's
/// own outputs (not the user's), used to generate advisory/gating signals
/// that leak forward into the NEXT turn's Behavior/Rhythm/Aperture inputs.
/// </summary>
public sealed record ModulationState
{
    public double EmotionalDrift { get; init; } = 0.0;
    public double RhythmMomentum { get; init; } = 0.0;
    public double GaitBias { get; init; } = 0.0;
    public double BehaviorBlend { get; init; } = 0.0;
    public double LastSentiment { get; init; } = 0.0;
    public double Attractor { get; init; } = 0.5;
    public int TurnCount { get; init; } = 0;
}

public sealed class StateManager
{
    public ModulationState State { get; private set; } = new();

    public void Reset() => State = new ModulationState();

    private static double QuickSentiment(string text)
    {
        var lowered = text.ToLowerInvariant();
        string[] positiveTokens = ["great", "awesome", "glad", "love", "nice", "!"];
        string[] negativeTokens = ["sorry", "concern", "worry", "bad", "confused", "?"];
        var positiveHits = positiveTokens.Count(lowered.Contains);
        var negativeHits = negativeTokens.Count(lowered.Contains);
        return Math.Clamp((positiveHits - negativeHits) / 6.0, -1.0, 1.0);
    }

    public void UpdateFromOutput(string output, RhythmState? rhythmState = null, string? gait = null)
    {
        var sentiment = QuickSentiment(output);
        var variability = rhythmState?.Variability ?? 0.0;
        var gaitBias = gait is "trot" or "gallop" or "sprint" ? 0.05 : gait == "idle" ? -0.05 : 0.0;
        var driftRate = 0.04 + variability * 0.12;

        var s = State;
        var emotionalDrift = Math.Clamp(s.EmotionalDrift * 0.9 + sentiment * driftRate, -0.35, 0.35);
        var rhythmMomentum = Math.Clamp(s.RhythmMomentum * 0.85 + (sentiment + gaitBias) * 0.25, -0.7, 0.7);
        var gaitBiasVal = Math.Clamp(s.GaitBias * 0.85 + gaitBias, -0.5, 0.5);
        var behaviorBlend = Math.Clamp(s.BehaviorBlend * 0.9 + sentiment * 0.2, -0.5, 0.7);
        var attractorTarget = 0.5 + rhythmMomentum * 0.15 + emotionalDrift * 0.2;
        var attractor = Math.Clamp(s.Attractor * 0.85 + attractorTarget * 0.15, 0.0, 1.0);

        State = new ModulationState
        {
            EmotionalDrift = emotionalDrift,
            RhythmMomentum = rhythmMomentum,
            GaitBias = gaitBiasVal,
            BehaviorBlend = behaviorBlend,
            LastSentiment = sentiment,
            Attractor = attractor,
            TurnCount = s.TurnCount + 1,
        };
    }

    public Dictionary<string, object?> AdvisoryPayload(double stabilityScore, double driftPressure)
    {
        var gatingBias = 0.0;
        if (stabilityScore < 0.25 || driftPressure > 0.6)
        {
            gatingBias = 0.6;
        }
        else if (stabilityScore < 0.4 || driftPressure > 0.4)
        {
            gatingBias = 0.3;
        }

        var blendWeight = 0.5 + State.BehaviorBlend * 0.5;
        return new Dictionary<string, object?>
        {
            ["gating_bias"] = Math.Clamp(gatingBias, 0.0, 1.0),
            ["blend_weight"] = Math.Clamp(blendWeight, 0.0, 1.0),
            ["emotional_drift"] = State.EmotionalDrift,
            ["rhythm_momentum"] = State.RhythmMomentum,
            ["gait_bias"] = State.GaitBias,
            ["attractor"] = State.Attractor,
        };
    }

    public Dictionary<string, object?> ExportSnapshot() => new()
    {
        ["emotional_drift"] = State.EmotionalDrift,
        ["rhythm_momentum"] = State.RhythmMomentum,
        ["gait_bias"] = State.GaitBias,
        ["behavior_blend"] = State.BehaviorBlend,
        ["last_sentiment"] = State.LastSentiment,
        ["attractor"] = State.Attractor,
        ["turn_count"] = State.TurnCount,
    };
}
