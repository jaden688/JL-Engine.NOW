namespace JLEngine.Core.Types;

/// <summary>
/// Port of Types.jl's `gear_modifiers`, plus a Phase 5 fix-up: the original
/// Julia function only recognizes "worm"/"cvt"/"planetary" (falling through
/// to the default for anything else, including Investment's "low"/"medium"/
/// "high"/"max" labels — a known gap documented during the faithful Phase 1
/// port). Per an explicit decision, Investment's vocabulary keeps its
/// human-readable labels for telemetry, and this class instead gained a
/// second, parallel tier table for them — Julia's own "worm"/"cvt"/
/// "planetary"/default behavior for real agent-card drive types is
/// untouched below.
/// </summary>
public static class Gear
{
    public const string DefaultGear = "spur";

    public static GearModifiers Modifiers(string? gear = null)
    {
        var key = string.IsNullOrEmpty(gear) ? DefaultGear : gear.ToLowerInvariant();
        return key switch
        {
            "worm" => new GearModifiers(0.6, 0.78, false),
            "cvt" => new GearModifiers(0.9, 0.55, true),
            "planetary" => new GearModifiers(1.0, 0.5, true),

            // Phase 5 addition: Investment's engagement tiers, a vocabulary
            // Julia's gear_modifiers never recognized. Monotonic with the
            // worm->planetary spectrum above (low investment reacts sluggishly,
            // max investment reacts almost instantly) but kept as distinct
            // constants rather than reusing worm/cvt/planetary's values.
            "low" => new GearModifiers(0.55, 0.80, false),
            "medium" => new GearModifiers(0.75, 0.68, false),
            "high" => new GearModifiers(0.88, 0.58, true),
            "max" => new GearModifiers(0.97, 0.48, true),

            _ => new GearModifiers(0.8, 0.65, false),
        };
    }
}

public sealed record GearModifiers(double ReactionSpeed, double ModeInertia, bool MultiMode);

public sealed record EngineConfig
{
    public string RootDir { get; init; } = Directory.GetCurrentDirectory();
    public string MasterFile { get; init; } = "JLframe_Engine_Framework.json";
    public string BehaviorStatesFile { get; init; } = "behavior_states.json";
    public string MpfRegistryFile { get; init; } = Path.Combine("agents", "Agents.mpf.json");
    public string AgentsDir { get; init; } = "agents";
    public bool SafetyOn { get; init; } = true;
    public string DefaultAgentName { get; init; } = "SparkByte";
    public int HistoryLength { get; init; } = 20;
}

public sealed record MpfProfile
{
    public required string AgentFile { get; init; }
    public string? DefaultMemoryMode { get; init; }
    public string? DefaultBackendId { get; init; }
    public string? DriveType { get; init; }
    public List<string> Tags { get; init; } = [];
}

public sealed record BehaviorState
{
    public string Id { get; init; } = "0,0";
    public string Name { get; init; } = "Unknown";
    public double Expressiveness { get; init; } = 0.5;
    public string Pacing { get; init; } = "normal";
    public string ToneBias { get; init; } = "neutral";
    public string MemoryStrictness { get; init; } = "medium";

    public override string ToString() => $"[{Id}] {Name}";

    /// <summary>
    /// Port of Types.jl's `instructions(state)`. Note: this is unused by
    /// Core.jl's own message-building in the Julia source (dead API
    /// surface) — ported faithfully for parity, but nothing calls it yet.
    /// </summary>
    public string Instructions() => string.Join("\n",
    [
        $"Current Behavior State: {Name} ({Id}).",
        $"- Expressiveness Level: {Math.Round(Expressiveness * 100, 1)}%",
        $"- Conversational Pacing: {Pacing}",
        $"- Dominant Tone: {ToneBias}",
        $"- Adherence to Memory: {MemoryStrictness}",
    ]);
}

public sealed record TurnSignals(
    double Sentiment,
    double Arousal,
    bool Directive,
    double Confusion,
    double Pace,
    double MemoryDensity);

public sealed record RhythmState(
    string Mode,
    double Index,
    double Variability,
    double Momentum,
    string Attractor,
    Dictionary<string, double> Modifiers,
    Dictionary<string, object?> Debug);

public sealed record DriftPressureInput
{
    public double AgentAlignmentScore { get; init; } = 1.0;
    public double BehaviorGridAlignmentScore { get; init; } = 1.0;
    public double SafetyAlignmentScore { get; init; } = 1.0;
    public double MemoryAlignmentScore { get; init; } = 1.0;
    public double ConversationalCoherenceScore { get; init; } = 1.0;
}

public sealed record DriftResponse(
    double Pressure,
    string ActionLevel,
    double TemperatureDelta,
    string? ForceGait,
    string? ForceRhythm,
    string? SupervisorWarning,
    bool ReinforceGait);
