using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JLEngine.Core.Config;
using JLEngine.Core.Engine;
using JLEngine.Persistence;
using JLEngine.Runtime.Tools;

namespace JLEngine.Runtime;

/// <summary>
/// Port of BYTE.jl's process_message / tool-calling loop — the BYTE-
/// equivalent runtime layer. Deliberately builds its OWN system prompt
/// (boot_prompt + cognitive-state block + self-context) independently of
/// JLEngineCore's `_build_messages`, matching Julia's actual dual-path
/// structure (Core.jl's builder is the "headless" path used by A2A; BYTE.jl
/// has always built its own richer prompt) — per the confirmed decision to
/// preserve that duplication rather than unify it.
/// </summary>
public sealed class AgentRuntime(
    JLEngineCore engine,
    ToolRegistry tools,
    Telemetry telemetry,
    HttpClient http,
    IReadOnlyDictionary<string, ToolSchemaEntry> builtinSchemas,
    SparkByteDatabase? db = null)
{
    // Matches Julia's actual source value (BYTE.jl:1013) — telemetry sampled
    // "12" in one trace, but that reflected the separate 4x-repeat guard
    // tripping first, not this ceiling. Per the plan, the source is ground truth.
    private const int MaxToolLoops = 30;
    private const int MaxRepeatToolCalls = 4;
    private const string DefaultModel = "deepseek/deepseek-v4-flash";
    private int _turnNumber;

    /// <summary>The OpenRouter model this runtime's tool-calling chat loop
    /// uses when no explicit model is passed to ProcessMessageAsync — settable
    /// live via the GUI's model picker (see RuntimeComposition's /api/model).</summary>
    public string CurrentModel { get; set; } = DefaultModel;

    /// <summary>Port of BYTE.jl's `_build_self_context`: a short description of the
    /// active agent's identity and declared tool posture, pulled from the raw
    /// agent-card JSON (loosely-typed, matching how the Julia source reads it).</summary>
    public string BuildSelfContext()
    {
        var data = engine.CurrentAgentData;
        var identity = data.GetOrDict("identity");
        var name = identity.GetOrString("name", engine.CurrentAgentName);
        var role = identity.GetOrString("role", "assistant");
        var description = identity.GetOrString("description", "");

        var toolPolicy = data.GetOrDict("core_tools")?.GetOrDict("tool_policy");
        var posture = toolPolicy.GetOrString("default_tool_posture", "standard");

        return $"SELF-CONTEXT: You are {name}, a {role}. {description} Default tool posture: {posture}.";
    }

    private string BuildSystemPrompt(TurnSnapshot snapshot)
    {
        var lines = new List<string>
        {
            engine.GetLlmBootPrompt(),
            "",
            "COGNITIVE STATE:",
            $"- Gait: {snapshot.Gait}",
            $"- Rhythm mode: {snapshot.Rhythm.Mode}",
            $"- Aperture mode: {snapshot.ApertureState.Mode} (temp={snapshot.ApertureState.Temp:F2}, top_p={snapshot.ApertureState.TopP:F2})",
            $"- Drift pressure: {Math.Round(snapshot.Drift.Pressure, 3)} ({snapshot.Drift.ActionLevel})",
            "",
            BuildSelfContext(),
        };
        return string.Join("\n", lines);
    }

    public async Task<string> ProcessMessageAsync(string userText, List<Dictionary<string, object?>> history, string model = DefaultModel)
    {
        var startedAt = DateTime.UtcNow;
        var snapshot = engine.AnalyzeTurn(userText);
        LogEngineSnapshot(snapshot);

        var systemPrompt = BuildSystemPrompt(snapshot);
        // Full prompt (not truncated) — the GUI's live terminal is meant to show
        // everything the engine is thinking, including exactly what it told the model.
        telemetry.LogSystemPrompt(engine.CurrentAgentName, systemPrompt, systemPrompt.Length);

        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "system", ["content"] = systemPrompt },
        };
        messages.AddRange(history);
        messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = userText });

        var toolsArray = tools.BuildOpenAiToolsArray(builtinSchemas);
        var temperature = Math.Clamp(snapshot.ApertureState.Temp + snapshot.Drift.TemperatureDelta, 0.1, 1.5);
        var topP = Math.Clamp(snapshot.ApertureState.TopP, 0.1, 1.0);

        var seenCalls = new List<string>();
        var finalReply = "";

        for (var loopIter = 1; ; loopIter++)
        {
            if (loopIter > MaxToolLoops)
            {
                telemetry.LogToolLoopGuard(engine.CurrentAgentName, model, loopIter, $"exceeded {MaxToolLoops} tool/API loops");
                finalReply = "[TOOL LOOP GUARD] Exceeded the maximum number of tool-call iterations for this turn.";
                break;
            }

            var (message, error) = await CallChatCompletionsAsync(model, messages, toolsArray, temperature, topP);
            if (error is not null)
            {
                telemetry.LogError("api_loop:iter_" + loopIter, error);
                finalReply = $"[ERROR: {error}]";
                break;
            }

            var toolCalls = message!.GetOrList("tool_calls");
            if (toolCalls is null || toolCalls.Count == 0)
            {
                finalReply = message.GetOrString("content", "");
                break;
            }

            messages.Add(message!);

            var loopShouldStop = false;
            foreach (var callObj in toolCalls)
            {
                if (callObj is not Dictionary<string, object?> call) continue;
                var callId = call.GetOrString("id");
                var function = call.GetOrDict("function") ?? [];
                var toolName = function.GetOrString("name");
                var argsJson = function.GetOrString("arguments", "{}");
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) ?? [];

                var signature = $"{toolName}:{argsJson}";
                seenCalls.Add(signature);
                if (seenCalls.Count(s => s == signature) > MaxRepeatToolCalls)
                {
                    telemetry.LogToolLoopGuard(engine.CurrentAgentName, model, loopIter, $"tool '{toolName}' called identically more than {MaxRepeatToolCalls} times");
                    finalReply = $"[TOOL LOOP GUARD] '{toolName}' was called identically too many times in a row.";
                    loopShouldStop = true;
                    break;
                }

                telemetry.LogToolCall(toolName, args, loopIter);
                var toolStarted = DateTime.UtcNow;
                var result = await tools.DispatchAsync(toolName, args, engine.CurrentAgentName);
                var elapsedMs = (long)(DateTime.UtcNow - toolStarted).TotalMilliseconds;
                telemetry.LogToolResult(toolName, result.ContainsKey("error"), elapsedMs);

                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = callId,
                    ["content"] = JsonSerializer.Serialize(result),
                });
            }

            if (loopShouldStop) break;
        }

        engine.RecordTurn(userText, finalReply, snapshot);
        var elapsedTotalMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        telemetry.LogTurnComplete(engine.CurrentAgentName, elapsedTotalMs);
        db?.WriteTurnSnapshot(snapshot, engine.CurrentAgentName, model, telemetry.SessionId, ++_turnNumber, userText.Length, finalReply.Length, elapsedTotalMs);
        return finalReply;
    }

    /// <summary>Logs Signals/Behavior/Rhythm/Drift/Investment/Aperture — the
    /// full "thought process" behind this turn's gait/tone/temperature choices
    /// — as one telemetry event, for the GUI's live terminal to display.</summary>
    private void LogEngineSnapshot(TurnSnapshot snapshot)
    {
        telemetry.LogEvent("engine_snapshot", new Dictionary<string, object?>
        {
            ["agent"] = snapshot.Agent,
            ["trigger"] = snapshot.Trigger,
            ["gait"] = snapshot.Gait,
            ["signals"] = new Dictionary<string, object?>
            {
                ["sentiment"] = snapshot.Signals.Sentiment,
                ["arousal"] = snapshot.Signals.Arousal,
                ["directive"] = snapshot.Signals.Directive,
                ["confusion"] = snapshot.Signals.Confusion,
                ["pace"] = snapshot.Signals.Pace,
                ["memoryDensity"] = snapshot.Signals.MemoryDensity,
            },
            ["behaviorState"] = new Dictionary<string, object?>
            {
                ["id"] = snapshot.BehaviorState.Id,
                ["name"] = snapshot.BehaviorState.Name,
                ["expressiveness"] = snapshot.BehaviorState.Expressiveness,
                ["pacing"] = snapshot.BehaviorState.Pacing,
                ["toneBias"] = snapshot.BehaviorState.ToneBias,
                ["memoryStrictness"] = snapshot.BehaviorState.MemoryStrictness,
            },
            ["rhythm"] = new Dictionary<string, object?>
            {
                ["mode"] = snapshot.Rhythm.Mode,
                ["index"] = snapshot.Rhythm.Index,
                ["variability"] = snapshot.Rhythm.Variability,
                ["momentum"] = snapshot.Rhythm.Momentum,
                ["attractor"] = snapshot.Rhythm.Attractor,
            },
            ["drift"] = new Dictionary<string, object?>
            {
                ["pressure"] = snapshot.Drift.Pressure,
                ["actionLevel"] = snapshot.Drift.ActionLevel,
                ["temperatureDelta"] = snapshot.Drift.TemperatureDelta,
                ["forceGait"] = snapshot.Drift.ForceGait,
                ["forceRhythm"] = snapshot.Drift.ForceRhythm,
                ["supervisorWarning"] = snapshot.Drift.SupervisorWarning,
            },
            ["investment"] = new Dictionary<string, object?>
            {
                ["level"] = snapshot.InvestmentLevel,
                ["gear"] = snapshot.InvestmentGear,
            },
            ["aperture"] = new Dictionary<string, object?>
            {
                ["mode"] = snapshot.ApertureState.Mode,
                ["score"] = snapshot.ApertureState.Score,
                ["temp"] = snapshot.ApertureState.Temp,
                ["topP"] = snapshot.ApertureState.TopP,
                ["focusLevel"] = snapshot.ApertureState.FocusLevel,
                ["overloadLevel"] = snapshot.ApertureState.OverloadLevel,
                ["emotion"] = snapshot.ApertureState.Emotion,
                ["driftBias"] = snapshot.ApertureState.DriftBias,
            },
            ["advisory"] = snapshot.Advisory,
        });
    }

    /// <summary>Caps a logged blob so one giant tool-schema/history dump can't
    /// stall the GUI's live-terminal socket; the full data still exists in the
    /// JSONL/SQLite telemetry sink, only the broadcast copy is capped.</summary>
    private static string TruncateForLog(string text, int max = 8000) =>
        text.Length > max ? text[..max] + $"...[truncated, {text.Length} chars total]" : text;

    private async Task<(Dictionary<string, object?>? Message, string? Error)> CallChatCompletionsAsync(
        string model, List<Dictionary<string, object?>> messages, List<Dictionary<string, object?>> toolsArray, double temperature, double topP)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            return (null, "OpenRouter API key is not set.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => (object?)m).ToList(),
            ["tools"] = toolsArray.Select(t => (object?)t).ToList(),
            ["tool_choice"] = "auto",
            ["temperature"] = temperature,
            ["top_p"] = topP,
        };
        var requestJson = JsonSerializer.Serialize(payload);

        // Real outbound network traffic, not a summary — the API key itself
        // lives only in the Authorization header below, never in this body,
        // but RedactSensitiveText still runs as cheap defense in depth.
        telemetry.LogEvent("api_request", new Dictionary<string, object?>
        {
            ["endpoint"] = "https://openrouter.ai/api/v1/chat/completions",
            ["model"] = model,
            ["body"] = Telemetry.RedactSensitiveText(TruncateForLog(requestJson)),
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request);
            var bodyText = await response.Content.ReadAsStringAsync();

            telemetry.LogEvent("api_response", new Dictionary<string, object?>
            {
                ["status"] = (int)response.StatusCode,
                ["body"] = Telemetry.RedactSensitiveText(TruncateForLog(bodyText)),
            });

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)response.StatusCode}: {bodyText}");
            }

            using var doc = JsonDocument.Parse(bodyText);
            var data = JsonLoader.Materialize(doc.RootElement) as Dictionary<string, object?> ?? [];
            var choices = data.GetOrList("choices");
            if (choices is not { Count: > 0 } || choices[0] is not Dictionary<string, object?> choice)
            {
                return (null, "Unexpected response format from OpenRouter.");
            }
            var message = choice.GetOrDict("message");
            return message is null ? (null, "Missing message in OpenRouter response.") : (message, null);
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }
}
