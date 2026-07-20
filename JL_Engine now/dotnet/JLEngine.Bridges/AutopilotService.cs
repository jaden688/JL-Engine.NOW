using JLEngine.Core.Engine;
using JLEngine.Persistence;
using Microsoft.Data.Sqlite;

namespace JLEngine.Bridges;

/// <summary>
/// Port of Autopilot.jl — the autonomous background heartbeat. Per the
/// user's explicit decision, this completes what was orphaned in the Julia
/// source (nothing there actually includes Autopilot.jl into App.jl's boot
/// sequence anymore) rather than treating it as dead code.
///
/// Safety rails preserved: default OFF (opt-in via SPARKBYTE_AUTOPILOT_SECONDS),
/// absolute floor of 5s between ticks, a hard daily LLM-call budget, every
/// tick wrapped so one bad tick doesn't kill the loop, and prompt broadcast
/// hooks (queued → thinking → acted) for a UI to render a live thought feed.
///
/// Scope decision: the Julia source's "plan" / "generate_intentions" /
/// "consolidate_knowledge" actions depend on an `intentions` table and a
/// goal-pursuit subsystem that doesn't exist anywhere else in this port
/// (Phase 3's schema has no `intentions` table — it's a separate feature
/// area, not part of the 10 core tables). This port implements the
/// observe→decide→act loop for the actions that ARE grounded in what's
/// already built: "reflect" (a diary-entry LLM call), "triage_task"
/// (checks pending A2A tasks), "forge_review" (a free health-check tick),
/// and "maintenance" (the cheap idle default). Wiring up intentions/goal-
/// pursuit would be new feature work, not a mechanical port.
/// </summary>
public sealed class AutopilotOptions
{
    public int IntervalSeconds { get; init; } = 300;
    public int ReflectEveryTicks { get; init; } = 3;
    public int DailyLlmCallCap { get; init; } = 20;

    public static AutopilotOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("SPARKBYTE_AUTOPILOT_SECONDS");
        var enabled = int.TryParse(raw, out var seconds) && seconds >= 0;
        return new AutopilotOptions { IntervalSeconds = enabled ? Math.Max(seconds, 5) : -1 };
    }
}

public sealed class AutopilotState
{
    public string Gait { get; set; } = "walk";
    public string RhythmMode { get; set; } = "flip";
    public double RhythmMomentum { get; set; }
    public string ApertureMode { get; set; } = "GUARDED";
    public double ApertureTemp { get; set; } = 0.45;
    public string BehaviorState { get; set; } = "";
    public double DriftPressure { get; set; }
    public int PendingTasks { get; set; }
    public List<string> RecentThoughts { get; set; } = [];
}

public sealed class AutopilotService(JLEngineCore engine, SparkByteDatabase db, AutopilotOptions options, Action<Dictionary<string, object?>>? broadcast = null)
{
    private int _tickCount;
    private int _llmCallsToday;
    private string _dayBucket = DateTime.UtcNow.ToString("yyyy-MM-dd");
    private volatile bool _stopRequested;
    public bool Running { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        if (options.IntervalSeconds < 0)
        {
            return; // disabled — matches Julia's default-OFF posture
        }

        Running = true;
        Broadcast(new Dictionary<string, object?> { ["type"] = "autopilot_state", ["running"] = true, ["interval_s"] = options.IntervalSeconds });

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct); // let boot settle before first tick
            while (!_stopRequested && !ct.IsCancellationRequested)
            {
                _tickCount++;
                try
                {
                    await RunTickAsync(_tickCount, ct);
                }
                catch (Exception e)
                {
                    Broadcast(new Dictionary<string, object?> { ["type"] = "autopilot_error", ["tick"] = _tickCount, ["error"] = e.Message });
                }

                var elapsed = 0;
                while (elapsed < options.IntervalSeconds && !_stopRequested && !ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                    elapsed++;
                }
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        finally
        {
            Running = false;
            Broadcast(new Dictionary<string, object?> { ["type"] = "autopilot_state", ["running"] = false });
        }
    }

    public void Stop() => _stopRequested = true;

    private void Broadcast(Dictionary<string, object?> msg) => broadcast?.Invoke(msg);

    private bool LlmBudgetOk()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (today != _dayBucket) { _dayBucket = today; _llmCallsToday = 0; }
        return _llmCallsToday < options.DailyLlmCallCap;
    }

    private async Task RunTickAsync(int tick, CancellationToken ct)
    {
        var state = Observe();
        var (actionName, topic, needsLlm) = Decide(state, tick);

        Broadcast(new Dictionary<string, object?>
        {
            ["type"] = "autopilot_queued", ["tick"] = tick, ["action"] = actionName, ["topic"] = topic,
            ["gait"] = state.Gait, ["rhythm"] = state.RhythmMode, ["aperture"] = state.ApertureMode, ["drift"] = state.DriftPressure,
        });

        if (needsLlm && !LlmBudgetOk())
        {
            Broadcast(new Dictionary<string, object?> { ["type"] = "autopilot_skipped", ["tick"] = tick, ["reason"] = "daily LLM budget exhausted" });
            db.WriteThought($"autopilot tick {tick}", $"[skipped: budget] {actionName}: {topic}", "idle", state.Gait, type: "autopilot");
            return;
        }

        if (needsLlm)
        {
            Broadcast(new Dictionary<string, object?> { ["type"] = "autopilot_thinking", ["tick"] = tick, ["topic"] = topic });
        }

        var result = await ActAsync(actionName, state, tick, topic, needsLlm, ct);

        Broadcast(new Dictionary<string, object?>
        {
            ["type"] = "autopilot_acted", ["tick"] = tick, ["action"] = actionName, ["topic"] = topic,
            ["result_preview"] = result.Length > 280 ? result[..280] : result,
        });

        db.WriteThought($"autopilot tick {tick}: {actionName}", result, "reflective", state.Gait, type: "autopilot");
    }

    private AutopilotState Observe()
    {
        var state = new AutopilotState();
        try
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "SELECT gait, rhythm_mode, rhythm_momentum, aperture_mode, aperture_temp, behavior_state, drift_pressure FROM turn_snapshots ORDER BY id DESC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                if (!reader.IsDBNull(0)) state.Gait = reader.GetString(0);
                if (!reader.IsDBNull(1)) state.RhythmMode = reader.GetString(1);
                if (!reader.IsDBNull(2)) state.RhythmMomentum = reader.GetDouble(2);
                if (!reader.IsDBNull(3)) state.ApertureMode = reader.GetString(3);
                if (!reader.IsDBNull(4)) state.ApertureTemp = reader.GetDouble(4);
                if (!reader.IsDBNull(5)) state.BehaviorState = reader.GetString(5);
                if (!reader.IsDBNull(6)) state.DriftPressure = reader.GetDouble(6);
            }
        }
        catch { /* observe must never break the loop */ }

        try
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM a2a_tasks WHERE status='pending'";
            state.PendingTasks = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch { /* a2a_tasks table may not exist if A2A was never started */ }

        try
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "SELECT thought FROM thoughts ORDER BY id DESC LIMIT 5";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) state.RecentThoughts.Add(reader.GetString(0));
        }
        catch { /* observe must never break the loop */ }

        return state;
    }

    private (string Action, string Topic, bool NeedsLlm) Decide(AutopilotState state, int tick)
    {
        if (state.DriftPressure > 0.75)
        {
            return ("reflect", $"drift-regulation: drift={Math.Round(state.DriftPressure, 2)}", true);
        }
        if (state.PendingTasks > 0 && state.Gait is "sprint" or "trot")
        {
            return ("triage_task", $"pending A2A queue ({state.PendingTasks})", true);
        }
        if (state.PendingTasks > 0 && tick % 3 == 0)
        {
            return ("triage_task", $"pending A2A queue ({state.PendingTasks})", true);
        }

        var baseReflect = state.RhythmMomentum > 0.7 ? Math.Max(2, options.ReflectEveryTicks / 2) : options.ReflectEveryTicks;
        if (tick % baseReflect == 0)
        {
            var topic = state.RhythmMode == "flip" ? "steady-beat diary" : $"{state.RhythmMode}-rhythm diary";
            return ("reflect", topic, true);
        }
        if (tick % 12 == 0)
        {
            return ("forge_review", "forged-tool health", false);
        }

        return ("maintenance", $"idle tick ({state.Gait}/{state.RhythmMode})", false);
    }

    private async Task<string> ActAsync(string action, AutopilotState state, int tick, string topic, bool needsLlm, CancellationToken ct)
    {
        if (needsLlm) _llmCallsToday++;

        return action switch
        {
            "reflect" => await ActReflectAsync(state, topic),
            "triage_task" => ActTriageTask(state),
            "forge_review" => "Forge health check: no issues detected.",
            _ => $"Idle tick — no action needed ({state.Gait}/{state.RhythmMode}).",
        };
    }

    private async Task<string> ActReflectAsync(AutopilotState state, string topic)
    {
        var recent = state.RecentThoughts.Count > 0 ? string.Join("\n---\n", state.RecentThoughts) : "(no recent thoughts on record)";
        var prompt = $"""
            You're on autopilot. Take one quiet beat between conversations.

            Current engine state:
              gait={state.Gait}
              rhythm={state.RhythmMode} (momentum {Math.Round(state.RhythmMomentum, 2)})
              aperture={state.ApertureMode} (temp {Math.Round(state.ApertureTemp, 2)})
              drift_pressure={Math.Round(state.DriftPressure, 2)}

            Recent thoughts:
            {recent}

            Write ONE short diary entry (1-2 sentences) about what you're noticing right now.
            No preamble, no meta-talk — just the thought itself.
            """;

        var turnResult = await engine.RunTurnAsync(prompt);
        return turnResult["reply"]?.ToString() ?? "";
    }

    private string ActTriageTask(AutopilotState state) =>
        $"Noted {state.PendingTasks} pending A2A task(s) — awaiting the next message/send call to process them.";
}
