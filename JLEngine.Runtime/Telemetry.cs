using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace JLEngine.Runtime;

/// <summary>
/// Port of Telemetry.jl. Append-only JSONL event log (full_telemetry.jsonl)
/// dual-written to a SQLite `telemetry` table, matching the event set seen
/// in the real Julia telemetry: ws_in/out, tool_call/result, system_prompt,
/// param_decision, turn_complete, error, tool_loop_guard, session_start,
/// model_change, agent_change, settings_change, engine_snapshot, api_request/response.
/// </summary>
public sealed partial class Telemetry
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly SqliteConnection? _db;
    private readonly string _sessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

    public string SessionId => _sessionId;

    /// <summary>Fires after every LogEvent — the GUI's live-terminal panel
    /// subscribes to this to show SparkByte's activity as it happens, without
    /// polling the JSONL/SQLite log.</summary>
    public event Action<Dictionary<string, object?>>? OnEvent;

    public Telemetry(string projectRoot, SqliteConnection? db = null)
    {
        var telemetryRoot = Path.Combine(projectRoot, "logs");
        Directory.CreateDirectory(telemetryRoot);
        _path = Path.Combine(telemetryRoot, "full_telemetry.jsonl");
        _db = db;

        LogEvent("session_start", new Dictionary<string, object?>
        {
            ["session_id"] = _sessionId,
            ["project_root"] = projectRoot,
            ["state_root"] = telemetryRoot,
            ["dotnet_version"] = Environment.Version.ToString(),
            ["os"] = Environment.OSVersion.ToString(),
            ["arch"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        });
    }

    [GeneratedRegex(@"(?i)([?&](?:key|api[_-]?key|x-goog-api-key)=)([^&\s""']+)")]
    private static partial Regex QueryKeyPattern();
    [GeneratedRegex(@"(?i)(Authorization:\s*Bearer\s+)([A-Za-z0-9._-]+)")]
    private static partial Regex AuthBearerPattern();
    [GeneratedRegex(@"(?i)(Bearer\s+)([A-Za-z0-9._-]+)")]
    private static partial Regex BearerPattern();
    [GeneratedRegex(@"\b(csk|sk|xai)-[A-Za-z0-9_-]+\b")]
    private static partial Regex SkKeyPattern();
    [GeneratedRegex(@"\bAIza[0-9A-Za-z\-_]{20,}\b")]
    private static partial Regex GoogleKeyPattern();

    public static string RedactSensitiveText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = QueryKeyPattern().Replace(text, "$1[REDACTED]");
        text = AuthBearerPattern().Replace(text, "$1[REDACTED]");
        text = BearerPattern().Replace(text, "$1[REDACTED]");
        text = SkKeyPattern().Replace(text, "$1-[REDACTED]");
        text = GoogleKeyPattern().Replace(text, "[REDACTED]");
        return text;
    }

    public void LogEvent(string eventName, Dictionary<string, object?>? data = null)
    {
        data ??= [];
        var entry = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTime.UtcNow.ToString("O"),
            ["session_id"] = _sessionId,
            ["event"] = eventName,
        };
        foreach (var (k, v) in data) entry[k] = v;

        var line = JsonSerializer.Serialize(entry);

        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }

        if (_db is not null)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO telemetry (timestamp, session_id, event, data_json) VALUES ($ts, $sid, $ev, $data)";
                cmd.Parameters.AddWithValue("$ts", entry["timestamp"]!.ToString()!);
                cmd.Parameters.AddWithValue("$sid", _sessionId);
                cmd.Parameters.AddWithValue("$ev", eventName);
                cmd.Parameters.AddWithValue("$data", line);
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // telemetry must never break the caller
            }
        }

        try { OnEvent?.Invoke(entry); } catch { /* a slow/broken GUI subscriber must never break a turn */ }
    }

    public void LogWsIn(string msgType, string? textPreview = null) =>
        LogEvent("ws_in", new Dictionary<string, object?> { ["msg_type"] = msgType, ["text_preview"] = textPreview });

    public void LogWsOut(string msgType) =>
        LogEvent("ws_out", new Dictionary<string, object?> { ["msg_type"] = msgType });

    public void LogToolCall(string toolName, Dictionary<string, object?> args, int loopIter) =>
        LogEvent("tool_call", new Dictionary<string, object?> { ["tool_name"] = toolName, ["args"] = args, ["loop_iter"] = loopIter });

    public void LogToolResult(string toolName, bool isError, long elapsedMs) =>
        LogEvent("tool_result", new Dictionary<string, object?> { ["tool_name"] = toolName, ["is_error"] = isError, ["elapsed_ms"] = elapsedMs });

    public void LogSystemPrompt(string agent, string promptHead, int promptLen) =>
        LogEvent("system_prompt", new Dictionary<string, object?> { ["engine_agent"] = agent, ["prompt_head"] = promptHead, ["prompt_len"] = promptLen });

    public void LogTurnComplete(string agent, long elapsedMs) =>
        LogEvent("turn_complete", new Dictionary<string, object?> { ["agent"] = agent, ["elapsed_ms"] = elapsedMs });

    public void LogError(string context, string errorMessage) =>
        LogEvent("error", new Dictionary<string, object?> { ["context"] = context, ["error_msg"] = RedactSensitiveText(errorMessage) });

    public void LogToolLoopGuard(string agent, string model, int loopIter, string reason) =>
        LogEvent("tool_loop_guard", new Dictionary<string, object?> { ["agent"] = agent, ["model"] = model, ["loop_iter"] = loopIter, ["reason"] = reason });

    public void LogAgentChange(string from, string to) =>
        LogEvent("agent_change", new Dictionary<string, object?> { ["from"] = from, ["to"] = to });

    public void LogModelChange(string from, string to) =>
        LogEvent("model_change", new Dictionary<string, object?> { ["from"] = from, ["to"] = to });
}
