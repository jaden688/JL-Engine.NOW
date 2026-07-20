using JLEngine.Core.Agents;
using JLEngine.Core.Aperture;
using JLEngine.Core.Backends;
using JLEngine.Core.Behavior;
using JLEngine.Core.Config;
using JLEngine.Core.Drift;
using JLEngine.Core.Investment;
using JLEngine.Core.Memory;
using JLEngine.Core.Mpf;
using JLEngine.Core.Rhythm;
using JLEngine.Core.Signals;
using JLEngine.Core.State;
using JLEngine.Core.Types;

namespace JLEngine.Core.Engine;

/// <summary>
/// A single turn's fully-computed state, mirroring Core.jl's `analyze_turn!`
/// snapshot dict. Uses typed sub-objects (rather than a raw dict) since the
/// snapshot's structure is entirely engine-controlled — unlike agent-card
/// JSON, there's no schema-drift risk to preserve by keeping it untyped.
/// </summary>
public sealed record TurnSnapshot(
    string Agent,
    string? AgentFile,
    Dictionary<string, object?> AgentProjection,
    string Trigger,
    string Gait,
    TurnSignals Signals,
    BehaviorState BehaviorState,
    Dictionary<string, object?>? BehaviorBlend,
    RhythmState Rhythm,
    DriftResponse Drift,
    double InvestmentLevel,
    string InvestmentGear,
    ApertureState ApertureState,
    Dictionary<string, object?> Advisory,
    List<string> CoreRules,
    Dictionary<string, object?> MemoryContext);

/// <summary>
/// Port of Core.jl's JLEngineCore — the per-turn orchestrator wiring every
/// sub-engine together. See AnalyzeTurn for the exact per-turn call order
/// (Signals -> trigger/gait -> Drift -> advisory -> Behavior -> Rhythm ->
/// Investment -> Aperture -> AgentManager.GetProjection -> snapshot).
/// </summary>
public sealed class JLEngineCore
{
    public EngineConfig Config { get; }
    public Dictionary<string, object?> MasterBlob { get; }
    public Dictionary<string, object?> MasterConfig { get; }
    public List<string> CoreRules { get; }
    public Dictionary<string, MpfProfile> MpfProfiles { get; }
    public Dictionary<string, object?> AgentState { get; }
    public BehaviorStateMachine BehaviorEngine { get; }
    public EmotionalAperture EmotionalAperture { get; }
    public InvestmentSystem InvestmentSystem { get; }
    public RhythmEngine RhythmEngine { get; }
    public HybridMemorySystem MemorySystem { get; }
    public StateManager StateManager { get; }
    public AgentManager AgentManager { get; }
    public BackendRegistry Backends { get; } = new();

    public string CurrentAgentName { get; private set; }
    public Dictionary<string, object?> CurrentAgentData { get; private set; } = [];
    public string? CurrentAgentFile { get; private set; }
    public string CurrentGait { get; private set; } = "walk";
    public string CurrentRhythmMode { get; private set; } = "flop";
    public double StabilityScore { get; private set; } = 0.5;

    /// <summary>Mirrors set_cognitive_callback! — fn(payload, eventName). Exceptions
    /// from the callback are always swallowed (telemetry must never break a turn).</summary>
    public Action<object, string>? CognitiveCallback { get; set; }

    /// <summary>Extension point for the BYTE-equivalent runtime (Phase 2) to inject
    /// its own richer self-context block into the system prompt — mirrors Julia's
    /// `isdefined(Main, :BYTE) && push!(lines, Main.BYTE._build_self_context(engine))`.
    /// Null by default (no self-context line), matching Core.jl running standalone.</summary>
    public Func<JLEngineCore, string>? SelfContextProvider { get; set; }

    public JLEngineCore(EngineConfig? config = null)
    {
        Config = config ?? new EngineConfig();

        var masterPath = JsonLoader.ResolvePath(Config.RootDir, Config.MasterFile);
        MasterBlob = JsonLoader.LoadJsonSafely(masterPath);
        MasterConfig = JsonLoader.LoadEngineConfig(masterPath);
        CoreRules = (MasterConfig.GetOrList("core_rules") ?? []).OfType<string>().ToList();
        MpfProfiles = MpfRegistry.LoadMpfRegistry(JsonLoader.ResolvePath(Config.RootDir, Config.MpfRegistryFile));
        AgentState = new Dictionary<string, object?> { ["emotion"] = null, ["emotion_meta"] = null };

        BehaviorEngine = new BehaviorStateMachine(JsonLoader.ResolvePath(Config.RootDir, Config.BehaviorStatesFile));
        EmotionalAperture = new EmotionalAperture(agentState: AgentState);
        InvestmentSystem = new InvestmentSystem();
        RhythmEngine = new RhythmEngine();
        MemorySystem = new HybridMemorySystem();
        StateManager = new StateManager();
        AgentManager = new AgentManager(Config.RootDir, Config.AgentsDir);

        CurrentAgentName = Config.DefaultAgentName;
        SetAgent(Config.DefaultAgentName);
    }

    public void SetCognitiveCallback(Action<object, string>? fn) => CognitiveCallback = fn;

    private void FireCallback(object payload, string eventName)
    {
        if (CognitiveCallback is null) return;
        try { CognitiveCallback(payload, eventName); } catch { /* telemetry must never break a turn */ }
    }

    public bool SetAgent(string agentName)
    {
        var selectedName = MpfProfiles.ContainsKey(agentName) ? agentName : Config.DefaultAgentName;
        if (!MpfProfiles.TryGetValue(selectedName, out var profile)) return false;

        CurrentAgentName = selectedName;
        CurrentAgentFile = profile.AgentFile;
        AgentState["emotion"] = null;
        AgentState["emotion_meta"] = null;

        var agentPath = JsonLoader.ResolvePath(Config.RootDir, Path.Combine(Config.AgentsDir, profile.AgentFile));
        CurrentAgentData = File.Exists(agentPath) ? MpfRegistry.LoadAgentFile(agentPath) : [];

        EmotionalAperture.SetAgentState(AgentState);
        EmotionalAperture.SetEmotionPalette(EmotionEntry.ParsePalette(CurrentAgentData.GetOrList("emotion_palette")));
        if (profile.DriveType is not null) EmotionalAperture.SetDriveType(profile.DriveType);
        AgentManager.SetActiveAgent(selectedName, CurrentAgentData, MpfProfiles);

        // Partial reset on agent switch: grid position/gait/rhythm-mode-label/
        // stability_score reset, but Rhythm momentum/attractor, StateManager,
        // and Memory persist across the switch — matching Julia exactly.
        CurrentGait = "walk";
        CurrentRhythmMode = "flop";
        StabilityScore = 0.5;
        return true;
    }

    public TurnSnapshot AnalyzeTurn(string userMessage, string? agentName = null, bool? safetyOn = null)
    {
        if (agentName is not null) SetAgent(agentName);
        var safety = safetyOn ?? Config.SafetyOn;

        var signals = SignalScorer.Score(userMessage);
        var trigger = DeriveTrigger(signals);
        var prevGait = CurrentGait;
        CurrentGait = InferGait(signals);

        FireCallback(new Dictionary<string, object?> { ["event"] = "gait_change", ["from"] = prevGait, ["to"] = CurrentGait }, "gait_change");

        var driftInput = new DriftPressureInput
        {
            AgentAlignmentScore = 1.0 - Math.Min(0.25, signals.Confusion * 0.2),
            BehaviorGridAlignmentScore = 1.0 - Math.Min(0.35, signals.Arousal * 0.15),
            SafetyAlignmentScore = safety ? 1.0 : 0.9,
            MemoryAlignmentScore = 1.0 - Math.Min(0.40, signals.MemoryDensity * 0.25),
            ConversationalCoherenceScore = 1.0 - Math.Min(0.60, signals.Confusion * 0.8),
        };
        var pressure = DriftPressureSystem.Calculate(driftInput);
        var driftResponse = DriftPressureSystem.GetResponseAction(pressure);
        var advisory = StateManager.AdvisoryPayload(StabilityScore, pressure);

        var gatingBias = (double)advisory["gating_bias"]!;
        var gatingAdvice = gatingBias > 0
            ? new Dictionary<string, object?> { ["level"] = "weak_block", ["weight"] = gatingBias }
            : new Dictionary<string, object?> { ["level"] = "allow", ["weight"] = 0.0 };
        var behaviorState = BehaviorEngine.TransitionByTrigger(trigger, CurrentGait, gatingAdvice);

        var rhythmState = RhythmEngine.Compute(
            lastMode: CurrentRhythmMode,
            trigger: trigger,
            gait: CurrentGait,
            behaviorState: behaviorState,
            driftPressure: pressure,
            safetyOn: safety,
            modulationHint: advisory);
        CurrentRhythmMode = rhythmState.Mode;

        // Investment -> dynamic gear selection: how invested the agent is in this
        // exchange (stakes/engagement, not valence) picks the aperture's gear for
        // this turn. Phase 5 fix: `selectedGear` now resolves to a real tier in
        // Gear.Modifiers (see Types.cs and InvestmentSystem's docs) instead of
        // silently falling through to the default every time.
        var investmentLevel = InvestmentSystem.UpdateInvestment(signals, rhythmState.Momentum, pressure);
        var selectedGear = Investment.InvestmentSystem.InvestmentGear(investmentLevel);
        EmotionalAperture.SetDriveType(selectedGear);

        EmotionalAperture.InjectDriftBias((double)advisory["emotional_drift"]!);
        var apertureState = EmotionalAperture.UpdateFromSignals(
            behaviorState: behaviorState,
            gait: CurrentGait,
            rhythm: rhythmState.Mode,
            agentVividness: 0.6,
            safetyMode: safety,
            driftPressure: pressure,
            userSentiment: signals.Sentiment,
            conversationPacing: signals.Pace,
            memoryDensity: signals.MemoryDensity);

        AgentManager.UpdateDynamicWeight(signals, rhythmState, apertureState);
        var agentProjection = AgentManager.GetProjection();

        var snapshot = new TurnSnapshot(
            Agent: CurrentAgentName,
            AgentFile: CurrentAgentFile,
            AgentProjection: agentProjection,
            Trigger: trigger,
            Gait: CurrentGait,
            Signals: signals,
            BehaviorState: behaviorState,
            BehaviorBlend: BehaviorEngine.CurrentBlend(),
            Rhythm: rhythmState,
            Drift: driftResponse,
            InvestmentLevel: Math.Round(investmentLevel, 3),
            InvestmentGear: selectedGear,
            ApertureState: apertureState,
            Advisory: advisory,
            CoreRules: CoreRules,
            MemoryContext: MemorySystem.GetContext(CurrentAgentName));

        FireCallback(snapshot, "analyze_done");
        return snapshot;
    }

    public Dictionary<string, object?> RecordTurn(string userMessage, string output, TurnSnapshot snapshot)
    {
        var engineState = new Dictionary<string, object?>
        {
            ["gait"] = CurrentGait,
            ["rhythm"] = CurrentRhythmMode,
            ["aperture_mode"] = EmotionalAperture.GetState().Mode,
            ["dynamic"] = StateManager.ExportSnapshot(),
            ["flags"] = new Dictionary<string, object?>(),
        };
        MemorySystem.UpdateAfterTurn(CurrentAgentName, userMessage, output, engineState);

        StateManager.UpdateFromOutput(output, snapshot.Rhythm, CurrentGait);
        EmotionalAperture.ApplyOutputFeedback(output, snapshot.Rhythm, CurrentGait);

        var lastSentiment = (double)StateManager.ExportSnapshot()["last_sentiment"]!;
        StabilityScore = Math.Clamp(0.55 - snapshot.Drift.Pressure * 0.25 + lastSentiment * 0.05, 0.1, 0.95);

        FireCallback(new Dictionary<string, object?> { ["stability"] = StabilityScore, ["reply_len"] = output.Length }, "record_turn");

        return MemorySystem.GetContext(CurrentAgentName);
    }

    public string GetLlmBootPrompt(string target = "generic_llm") => MpfRegistry.GetLlmBootPrompt(CurrentAgentData, target);

    public async Task<Dictionary<string, object?>> RunTurnAsync(
        string userMessage,
        string? agentName = null,
        string? backendId = null,
        Dictionary<string, object?>? backendOverrides = null)
    {
        var snapshot = AnalyzeTurn(userMessage, agentName);
        var messages = BuildMessages(userMessage, snapshot);

        var options = new Dictionary<string, object?>
        {
            ["temperature"] = Math.Clamp(snapshot.ApertureState.Temp + snapshot.Drift.TemperatureDelta, 0.1, 1.5),
            ["top_p"] = Math.Clamp(snapshot.ApertureState.TopP, 0.1, 1.0),
        };

        var backend = backendId is null ? Backends.GetBrainBackend() : Backends.GetBackend(backendId, backendOverrides);
        var (reply, backendMeta) = await backend.GenerateAsync(messages, options);
        var context = RecordTurn(userMessage, reply, snapshot);

        FireCallback(new Dictionary<string, object?> { ["reply_len"] = reply.Length, ["stability"] = StabilityScore }, "run_turn");

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["reply"] = reply,
            ["telemetry"] = new Dictionary<string, object?> { ["snapshot"] = snapshot, ["backend_meta"] = backendMeta, ["messages"] = messages },
            ["memory_context"] = context,
        };
    }

    private static string DeriveTrigger(TurnSignals signals)
    {
        if (signals.Sentiment > 0.5 && signals.Arousal > 0.5) return "user_hyped";
        if (signals.Sentiment < -0.3 && signals.Arousal > 0.3) return "user_frustrated";
        if (signals.Confusion > 0.6) return "user_confused";
        if (signals.Sentiment < -0.4 && signals.Arousal > 0.2) return "user_distressed";
        if (signals.Directive) return "user_directive";
        return "neutral";
    }

    private static string InferGait(TurnSignals signals)
    {
        if (signals.Confusion > 0.7 && signals.Sentiment < 0) return "idle";
        if (signals.Arousal >= 0.85) return "sprint";
        if (signals.Arousal >= 0.60) return "trot";
        return "walk";
    }

    private List<Dictionary<string, object?>> BuildMessages(string userText, TurnSnapshot snapshot)
    {
        var projection = snapshot.AgentProjection;
        var lines = new List<string>();

        if (CoreRules.Count > 0)
        {
            lines.Add("CORE RULES:");
            lines.AddRange(CoreRules.Select(rule => $"- {rule}"));
        }
        lines.Add("");
        lines.Add($"ACTIVE AGENT: {projection.GetOr("name") as string ?? CurrentAgentName}");

        if (projection.GetOr("base_prompt") is string basePrompt && basePrompt.Length > 0)
        {
            lines.Add(basePrompt);
        }
        lines.Add("");

        if (SelfContextProvider is not null)
        {
            lines.Add(SelfContextProvider(this));
            lines.Add("");
        }

        lines.Add("ENGINE STATE SNAPSHOT:");
        lines.Add($"- Gait: {snapshot.Gait}");
        lines.Add($"- Rhythm mode: {snapshot.Rhythm.Mode}");
        lines.Add($"- Aperture mode: {snapshot.ApertureState.Mode}");
        lines.Add($"- Drift pressure: {Math.Round(snapshot.Drift.Pressure, 3)}");
        lines.Add($"- Stability score: {Math.Round(StabilityScore, 3)}");

        var history = new List<Dictionary<string, object?>>();
        var agentMemory = snapshot.MemoryContext.GetOrDict("agent_memory");
        if (agentMemory?.GetOrList("recent_interactions") is { } recent)
        {
            foreach (var interactionObj in recent.TakeLast(3))
            {
                if (interactionObj is not Dictionary<string, object?> interaction) continue;
                history.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = interaction.GetOr("user_message", "") });
                history.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = interaction.GetOr("output", "") });
            }
        }

        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "system", ["content"] = string.Join("\n", lines) },
        };
        messages.AddRange(history);
        messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = userText });
        return messages;
    }
}
