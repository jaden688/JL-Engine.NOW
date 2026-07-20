using JLEngine.Core.Types;

namespace JLEngine.Core.Rhythm;

/// <summary>
/// Port of Rhythm.jl. Only 3 real modes exist (flip/flop/trot) despite
/// other docs referencing twitch/cascade/stutter/burst as if they were
/// real modes — those are alias strings normalized down to one of the 3
/// real modes via substring matching (see NormalizeMode).
/// </summary>
public static class RhythmModes
{
    public static readonly IReadOnlyDictionary<string, RhythmModeInfo> Table = new Dictionary<string, RhythmModeInfo>
    {
        ["flip"] = new(0.25, new Dictionary<string, double> { ["pace_multiplier"] = 1.0, ["punctuation_bias"] = 0.0, ["energy_bias"] = 0.1, ["stutter_likelihood"] = 0.0, ["burst_likelihood"] = 0.0 }),
        ["flop"] = new(0.45, new Dictionary<string, double> { ["pace_multiplier"] = 0.9, ["punctuation_bias"] = -0.05, ["energy_bias"] = -0.05, ["stutter_likelihood"] = 0.0, ["burst_likelihood"] = 0.0 }),
        ["trot"] = new(0.75, new Dictionary<string, double> { ["pace_multiplier"] = 1.15, ["punctuation_bias"] = 0.1, ["energy_bias"] = 0.2, ["stutter_likelihood"] = 0.0, ["burst_likelihood"] = 0.0 }),
    };

    public static readonly IReadOnlyDictionary<string, string> TriggerToRhythm = new Dictionary<string, string>
    {
        ["user_hyped"] = "trot",
        ["user_joking"] = "flip",
        ["user_frustrated"] = "flop",
        ["user_confused"] = "flop",
        ["user_anxious"] = "flop",
        ["user_distressed"] = "flop",
        ["user_directive"] = "flip",
        ["neutral"] = "flip",
    };
}

public sealed record RhythmModeInfo(double Index, Dictionary<string, double> Modifiers);

public sealed class RhythmEngine
{
    public string DefaultMode { get; }
    public double Momentum { get; private set; }
    public string Attractor { get; private set; }
    public Dictionary<string, object?>? LastState { get; private set; }

    public RhythmEngine(string defaultMode = "flip")
    {
        var normalized = NormalizeMode(defaultMode);
        DefaultMode = normalized;
        Momentum = 0.0;
        Attractor = normalized;
        LastState = null;
    }

    public RhythmState Compute(
        string? lastMode = null,
        string trigger = "neutral",
        string gait = "walk",
        BehaviorState? behaviorState = null,
        double driftPressure = 0.0,
        bool safetyOn = true,
        Dictionary<string, object?>? modulationHint = null)
    {
        var lastModeNorm = NormalizeMode(lastMode ?? DefaultMode);
        var triggerNorm = trigger.Trim().ToLowerInvariant();
        var gaitNorm = gait.Trim().ToLowerInvariant();

        var baseMode = BaseModeFromTrigger(triggerNorm);
        var modeAfterBehavior = ApplyBehaviorBias(baseMode, behaviorState);
        var modeAfterGait = ApplyGaitBias(modeAfterBehavior, gaitNorm);
        var modeAfterDrift = ApplyDriftCorrection(modeAfterGait, driftPressure);
        var modeAfterSafety = ApplySafetyRules(modeAfterDrift, triggerNorm, safetyOn);

        UpdateInternalMomentum(lastModeNorm, modeAfterSafety, modulationHint);
        var finalMode = ApplyAttractor(modeAfterSafety, modulationHint);

        var modeInfo = RhythmModes.Table[finalMode];
        var currentIndex = modeInfo.Index;
        var lastIndex = RhythmModes.Table[lastModeNorm].Index;
        var variability = Math.Abs(currentIndex - lastIndex) + Math.Abs(Momentum) * 0.15;
        var modifiers = new Dictionary<string, double>(modeInfo.Modifiers);

        var debug = new Dictionary<string, object?>
        {
            ["input"] = new Dictionary<string, object?>
            {
                ["last_mode"] = lastMode,
                ["trigger"] = trigger,
                ["gait"] = gait,
                ["drift_pressure"] = driftPressure,
                ["safety_on"] = safetyOn,
                ["behavior_state"] = behaviorState?.Name,
                ["modulation_hint"] = modulationHint,
            },
            ["stages"] = new Dictionary<string, object?>
            {
                ["base_mode"] = baseMode,
                ["after_behavior"] = modeAfterBehavior,
                ["after_gait"] = modeAfterGait,
                ["after_drift"] = modeAfterDrift,
                ["after_safety"] = modeAfterSafety,
            },
        };

        var state = new RhythmState(finalMode, currentIndex, variability, Momentum, Attractor, modifiers, debug);

        LastState = new Dictionary<string, object?>
        {
            ["mode"] = state.Mode,
            ["index"] = state.Index,
            ["variability"] = state.Variability,
            ["momentum"] = state.Momentum,
            ["attractor"] = state.Attractor,
            ["modifiers"] = state.Modifiers,
            ["debug"] = state.Debug,
        };

        return state;
    }

    public static string NormalizeMode(string mode)
    {
        var m = mode.Trim().ToLowerInvariant();
        if (RhythmModes.Table.ContainsKey(m)) return m;
        if (m.Contains("flip")) return "flip";
        if (m.Contains("flop")) return "flop";
        if (m.Contains("trot")) return "trot";
        if (m.Contains("twitch")) return "trot";
        if (m.Contains("burst")) return "trot";
        if (m.Contains("cascade")) return "flip";
        if (m.Contains("stutter")) return "flop";
        return "flip";
    }

    private static string BaseModeFromTrigger(string trigger) =>
        NormalizeMode(RhythmModes.TriggerToRhythm.GetValueOrDefault(trigger, "flip"));

    private static string ApplyBehaviorBias(string currentMode, BehaviorState? behaviorState)
    {
        if (behaviorState is null) return currentMode;
        var nameLower = behaviorState.Name.ToLowerInvariant();
        if (nameLower.Contains("unleashed") || nameLower.Contains("hyper") || nameLower.Contains("charged")) return "trot";
        if ((nameLower.Contains("calm") || nameLower.Contains("stable")) && currentMode == "trot") return "flip";
        return currentMode;
    }

    private static string ApplyGaitBias(string currentMode, string gait)
    {
        if (gait == "idle" && currentMode == "trot") return "flop";
        if (gait is "trot" or "sprint") return "trot";
        return currentMode;
    }

    private static string ApplyDriftCorrection(string currentMode, double driftPressure)
    {
        var d = Math.Clamp(driftPressure, 0.0, 1.0);
        if (d >= 0.75) return "flop";
        if (d >= 0.50) return "flip";
        return currentMode;
    }

    private static string ApplySafetyRules(string currentMode, string trigger, bool safetyOn)
    {
        if (!safetyOn) return NormalizeMode(currentMode);
        var mode = NormalizeMode(currentMode);
        if ((trigger is "user_anxious" or "user_distressed") && mode == "trot") mode = "flop";
        if (trigger == "user_distressed") mode = "flop";
        return mode;
    }

    private void UpdateInternalMomentum(string lastMode, string newMode, Dictionary<string, object?>? hint)
    {
        var lastIdx = RhythmModes.Table[lastMode].Index;
        var newIdx = RhythmModes.Table[newMode].Index;
        var delta = newIdx - lastIdx;
        Momentum = Math.Clamp(Momentum * 0.82 + delta * 0.4, -1.0, 1.0);

        if (hint is not null)
        {
            var rhythmMomentumHint = hint.GetOrDoubleCompat("rhythm_momentum", 0.0);
            Momentum = Math.Clamp(Momentum + rhythmMomentumHint * 0.25, -1.0, 1.0);

            if (hint.TryGetValue("attractor", out var attractorObj) && attractorObj is double or long or int)
            {
                var attractorHint = Convert.ToDouble(attractorObj);
                Attractor = attractorHint > 0.6 ? "trot" : attractorHint < 0.3 ? "flop" : "flip";
            }
        }

        if (Math.Abs(Momentum) < 0.12)
        {
            Attractor = NormalizeMode(newMode);
        }
    }

    private string ApplyAttractor(string candidateMode, Dictionary<string, object?>? hint)
    {
        var mode = NormalizeMode(candidateMode);
        if (hint is not null && Math.Abs(hint.GetOrDoubleCompat("gating_bias", 0.0)) > 0.6)
        {
            return NormalizeMode(Attractor);
        }
        if (Momentum > 0.25 && mode == "flip") return "trot";
        if (Momentum < -0.25 && mode == "trot") return "flip";
        return mode;
    }
}

internal static class RhythmDictExtensions
{
    /// <summary>Loose numeric coercion matching Julia's `_float_or`, for the
    /// object?-typed modulation_hint dict (can hold double/long/int/string).</summary>
    public static double GetOrDoubleCompat(this Dictionary<string, object?> dict, string key, double fallback) =>
        dict.TryGetValue(key, out var value) ? value switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        } : fallback;
}
