using JLEngine.Core.Config;
using JLEngine.Core.Types;

namespace JLEngine.Core.Aperture;

/// <summary>
/// Port of Aperture.jl. This is the engine's temperature/top_p/creativity
/// modulation system: it computes a 0-1 "openness" score from weighted
/// signals, maps it to a named mode with sampling modifiers, and applies
/// a hard safety ceiling whenever safety_mode is on.
///
/// Faithful port: the safety ceiling `Math.Min(score, 0.60)` is applied at
/// BOTH the same two points as Julia (inside Compute, and again after the
/// gear-based blend in UpdateFromSignals) so a prior high-openness turn
/// can't leak past the cap through blending inertia.
/// </summary>
public static class ApertureModifiers
{
    public static readonly IReadOnlyDictionary<string, ApertureModifierSet> Table = new Dictionary<string, ApertureModifierSet>
    {
        ["CLOSED"] = new(0.10, 0.20, 0.05, 0.05, 0.06),
        ["GUARDED"] = new(0.25, 0.45, 0.20, 0.18, 0.22),
        ["BALANCED"] = new(0.45, 0.70, 0.45, 0.45, 0.50),
        ["OPEN"] = new(0.65, 0.85, 0.70, 0.75, 0.78),
        ["WIDE_OPEN"] = new(0.85, 0.95, 0.95, 0.98, 1.00),
    };
}

public sealed record ApertureModifierSet(
    double Temperature,
    double TopP,
    double AgentAmplitude,
    double CreativityBias,
    double Expressiveness);

public sealed record EmotionEntry(
    string? Id,
    string? Label,
    string? Style,
    List<double>? ScoreRange,
    double Intensity,
    string Sentiment,
    Dictionary<string, object?>? SamplingBias)
{
    /// <summary>Parses one raw JSON entry from an agent card's `emotion_palette`
    /// array (Dictionary form) into a typed EmotionEntry, mirroring the
    /// defensive field reads Aperture.jl performs inline.</summary>
    public static EmotionEntry FromDict(Dictionary<string, object?> data) => new(
        Id: data.GetOr("id") as string,
        Label: data.GetOr("label") as string,
        Style: data.GetOr("style") as string,
        ScoreRange: (data.GetOrList("score_range") ?? []).Select(v => v switch
        {
            double d => d,
            long l => (double)l,
            int i => (double)i,
            _ => 0.0,
        }).ToList(),
        Intensity: data.GetOrDouble("intensity", 0.5),
        Sentiment: data.GetOrString("sentiment", "any"),
        SamplingBias: data.GetOrDict("sampling_bias"));

    public static List<EmotionEntry> ParsePalette(List<object?>? rawPalette) =>
        (rawPalette ?? [])
            .OfType<Dictionary<string, object?>>()
            .Select(FromDict)
            .ToList();
}

public sealed record ApertureState(
    double Score,
    string Mode,
    ApertureModifierSet Modifiers,
    double Temp,
    double TopP,
    double FocusLevel,
    double OverloadLevel,
    string? Emotion,
    EmotionEntry? EmotionMeta,
    double DriftBias,
    string? DriveType = null,
    double? GearAlpha = null);

public sealed class EmotionalAperture(string driveType = "spur", Dictionary<string, object?>? agentState = null)
{
    public string DriveType { get; private set; } = driveType;
    public string? CurrentEmotion { get; private set; }
    public EmotionEntry? CurrentEmotionMeta { get; private set; }
    public Dictionary<string, object?>? AgentState { get; private set; } = agentState;
    public List<EmotionEntry> EmotionPalette { get; private set; } = [];
    public double FocusLevel { get; private set; }
    public double OverloadLevel { get; private set; }
    public double DriftBias { get; private set; }
    public double RecentSentiment { get; private set; }
    public ApertureState LastState { get; private set; } = BuildState(0.25, "GUARDED", ApertureModifiers.Table["GUARDED"], 0.0, 0.0, null, null, 0.0);

    public void SetDriveType(string newDriveType) => DriveType = newDriveType;

    public void SetEmotionPalette(List<EmotionEntry>? palette)
    {
        EmotionPalette = palette ?? [];
        if (palette is null)
        {
            CurrentEmotion = null;
            CurrentEmotionMeta = null;
            WriteAgentEmotion(null, null);
        }
    }

    public void SetAgentState(Dictionary<string, object?>? state) => AgentState = state;

    public void Reset()
    {
        CurrentEmotion = null;
        CurrentEmotionMeta = null;
        FocusLevel = 0.0;
        OverloadLevel = 0.0;
        DriftBias = 0.0;
        RecentSentiment = 0.0;
        LastState = BuildState(0.25, "GUARDED", ApertureModifiers.Table["GUARDED"], 0.0, 0.0, null, null, 0.0);
        WriteAgentEmotion(null, null);
    }

    public ApertureState GetState() => LastState;
    public double GetFocusLevel() => FocusLevel;
    public double GetOverloadLevel() => OverloadLevel;

    public ApertureState UpdateFromSignals(
        BehaviorState? behaviorState,
        string gait = "walk",
        string rhythm = "flop",
        double agentVividness = 0.6,
        bool safetyMode = true,
        double driftPressure = 0.0,
        double driftBiasIn = 0.0,
        double userSentiment = 0.0,
        double conversationPacing = 0.5,
        double memoryDensity = 0.0,
        double apertureBias = 0.0)
    {
        var behaviorIntensity = behaviorState?.Expressiveness ?? 0.5;
        var signals = new ApertureSignals(
            BehaviorIntensity: behaviorIntensity,
            AgentVividness: agentVividness,
            SafetyMode: safetyMode,
            DriftPressure: driftPressure,
            DriftBias: driftBiasIn,
            UserSentiment: userSentiment,
            ConversationPacing: conversationPacing,
            MemoryDensity: memoryDensity,
            GaitRange: MapGaitToRange(gait),
            RhythmVariability: MapRhythmToVariability(rhythm),
            ApertureBias: apertureBias);

        var computed = Compute(signals);

        // Gear dynamics: drive_type governs HOW the aperture moves toward the
        // signal-computed target, not where the target lands. worm = sluggish,
        // planetary = snappy. Same easing applied to focus/overload.
        var gear = Gear.Modifiers(DriveType);
        var alpha = Math.Clamp(gear.ReactionSpeed * (1.0 - gear.ModeInertia), 0.0, 1.0);

        var targetScore = computed.Score;
        var prevScore = LastState.Score;
        var blendedScore = Math.Clamp(prevScore + alpha * (targetScore - prevScore), 0.0, 1.0);
        // Re-assert the safety ceiling after blending: a prior high-openness
        // turn must not leak past a freshly-enabled safety cap through inertia.
        if (signals.SafetyMode)
        {
            blendedScore = Math.Min(blendedScore, 0.60);
        }

        var mode = ModeFromScore(blendedScore);
        var modifiers = ApertureModifiers.Table[mode];

        var (targetFocus, targetOverload) = DeriveFocusOverload(signals);
        FocusLevel = Math.Clamp(FocusLevel + alpha * (targetFocus - FocusLevel), 0.0, 1.0);
        OverloadLevel = Math.Clamp(OverloadLevel + alpha * (targetOverload - OverloadLevel), 0.0, 1.0);

        LastState = BuildState(blendedScore, mode, modifiers, FocusLevel, OverloadLevel, CurrentEmotion, CurrentEmotionMeta, DriftBias)
            with { DriveType = DriveType, GearAlpha = Math.Round(alpha, 3) };

        var selectedEmotion = SelectEmotion(LastState.Score, signals, behaviorState);
        ApplySelectedEmotion(selectedEmotion);
        return LastState;
    }

    public void ApplyOutputFeedback(string outputText, RhythmState? rhythmState = null, string? gait = null)
    {
        var sentiment = QuickSentiment(outputText);
        var variability = rhythmState?.Variability ?? 0.0;
        var gaitPush = 0.0;

        if (gait is not null)
        {
            var gaitLower = gait.ToLowerInvariant();
            if (gaitLower is "trot" or "gallop" or "sprint") gaitPush = 0.05;
            if (gaitLower == "idle") gaitPush = -0.05;
        }

        var driftRate = 0.015 + variability * 0.08 + Math.Abs(gaitPush) * 0.5;
        DriftBias = Math.Clamp(DriftBias * 0.9 + sentiment * driftRate, -0.25, 0.25);
        RecentSentiment = sentiment;
        FocusLevel = Math.Clamp(FocusLevel + Math.Max(0.0, sentiment) * 0.05, 0.0, 1.0);
        OverloadLevel = Math.Clamp(OverloadLevel + Math.Max(0.0, -sentiment) * 0.05, 0.0, 1.0);
        LastState = LastState with { DriftBias = DriftBias };
    }

    public void InjectDriftBias(double bias) => DriftBias = Math.Clamp(bias, -0.35, 0.35);

    private (double Score, string Mode, ApertureModifierSet Modifiers) Compute(ApertureSignals signals)
    {
        var score =
            signals.BehaviorIntensity * 0.18 +
            signals.AgentVividness * 0.16 +
            signals.UserSentiment * 0.22 +
            signals.ConversationPacing * 0.08 +
            signals.MemoryDensity * 0.12 +
            signals.GaitRange * 0.06 +
            signals.RhythmVariability * 0.08 -
            signals.DriftPressure * 0.20;

        score += signals.ApertureBias;
        score += signals.DriftBias;
        score += DriftBias;
        score = Math.Clamp(score, 0.0, 1.0);

        if (signals.SafetyMode)
        {
            score = Math.Min(score, 0.60);
        }

        var mode = ModeFromScore(score);
        return (score, mode, ApertureModifiers.Table[mode]);
    }

    private static (double Focus, double Overload) DeriveFocusOverload(ApertureSignals s)
    {
        var focus =
            s.BehaviorIntensity * 0.45 +
            (1.0 - s.RhythmVariability) * 0.20 +
            Math.Max(0.0, s.ConversationPacing - 0.4) * 0.15 +
            Math.Max(0.0, s.AgentVividness - 0.3) * 0.10 +
            Math.Max(0.0, s.UserSentiment) * 0.10 -
            s.DriftPressure * 0.20;

        var overload =
            s.DriftPressure * 0.35 +
            s.MemoryDensity * 0.25 +
            s.GaitRange * 0.10 +
            s.RhythmVariability * 0.10 +
            Math.Max(0.0, -s.UserSentiment) * 0.12 +
            Math.Max(0.0, 0.5 - s.ConversationPacing) * 0.08;

        return (Math.Clamp(focus, 0.0, 1.0), Math.Clamp(overload, 0.0, 1.0));
    }

    private static double QuickSentiment(string text)
    {
        var lowered = text.ToLowerInvariant();
        string[] positives = ["great", "glad", "yes", "sure", "love", "!"];
        string[] negatives = ["sorry", "no", "cannot", "frustrated", "confused", "?"];
        var pos = positives.Count(lowered.Contains);
        var neg = negatives.Count(lowered.Contains);
        return Math.Clamp((pos - neg) / 6.0, -1.0, 1.0);
    }

    private static ApertureState BuildState(
        double score, string mode, ApertureModifierSet modifiers, double focusLevel, double overloadLevel,
        string? emotion, EmotionEntry? emotionMeta, double driftBias) =>
        new(score, mode, modifiers, modifiers.Temperature, modifiers.TopP, focusLevel, overloadLevel, emotion, emotionMeta, driftBias);

    private static string ModeFromScore(double score) => score switch
    {
        <= 0.12 => "CLOSED",
        <= 0.28 => "GUARDED",
        <= 0.55 => "BALANCED",
        <= 0.78 => "OPEN",
        _ => "WIDE_OPEN",
    };

    private static double MapGaitToRange(string gait) => gait.ToLowerInvariant() switch
    {
        "idle" => 0.1,
        "trot" => 0.55,
        "gallop" => 0.75,
        "sprint" => 0.9,
        _ => 0.3,
    };

    private static double MapRhythmToVariability(string rhythm) => rhythm.ToLowerInvariant() switch
    {
        "flop" => 0.2,
        "flip" => 0.35,
        "twitch" => 0.55,
        "cascade" => 0.45,
        "stutter" => 0.3,
        "burst" => 0.65,
        _ => 0.4,
    };

    private EmotionEntry? SelectEmotion(double score, ApertureSignals signals, BehaviorState? behaviorState)
    {
        if (EmotionPalette.Count == 0) return null;

        var sentiment = signals.UserSentiment;
        var behaviorIntensity = behaviorState?.Expressiveness ?? signals.BehaviorIntensity;

        EmotionEntry? best = null;
        var bestScore = -1.0;

        foreach (var entry in EmotionPalette)
        {
            var range = entry.ScoreRange is { Count: >= 2 } r ? r : [0.0, 1.0];
            var min = range[0];
            var max = range[1];
            if (min > max) (min, max) = (max, min);
            var span = Math.Max(0.1, max - min);
            var center = min + span / 2.0;
            var scoreFit = Math.Max(0.0, 1.0 - Math.Abs(score - center) / (span / 2.0));

            var targetIntensity = entry.Intensity;
            var intensityFit = Math.Max(0.0, 1.0 - Math.Abs(behaviorIntensity - targetIntensity));

            var sentimentPref = entry.Sentiment.ToLowerInvariant();
            var sentimentFit = 1.0;
            if (sentimentPref != "any")
            {
                sentimentFit = sentimentPref switch
                {
                    "positive" => sentiment >= 0.1 ? 1.0 : 0.55,
                    "negative" => sentiment <= -0.1 ? 1.0 : 0.55,
                    "neutral" => Math.Abs(sentiment) < 0.25 ? 1.0 : 0.55,
                    _ => 1.0,
                };
            }

            var combined = scoreFit * 0.5 + intensityFit * 0.3 + sentimentFit * 0.2;
            if (combined > bestScore)
            {
                bestScore = combined;
                best = entry;
            }
        }

        return best;
    }

    private void ApplySelectedEmotion(EmotionEntry? entry)
    {
        if (entry is null)
        {
            CurrentEmotion = null;
            CurrentEmotionMeta = null;
            LastState = LastState with { Emotion = null, EmotionMeta = null };
            WriteAgentEmotion(null, null);
            return;
        }

        var label = entry.Label ?? entry.Id;
        CurrentEmotion = label;
        CurrentEmotionMeta = entry;
        LastState = LastState with { Emotion = CurrentEmotion, EmotionMeta = entry };
        WriteAgentEmotion(CurrentEmotion, entry);
    }

    private void WriteAgentEmotion(string? label, EmotionEntry? meta)
    {
        if (AgentState is null) return;
        AgentState["emotion"] = label;
        AgentState["emotion_meta"] = meta;
    }

    private sealed record ApertureSignals(
        double BehaviorIntensity,
        double AgentVividness,
        bool SafetyMode,
        double DriftPressure,
        double DriftBias,
        double UserSentiment,
        double ConversationPacing,
        double MemoryDensity,
        double GaitRange,
        double RhythmVariability,
        double ApertureBias);
}
