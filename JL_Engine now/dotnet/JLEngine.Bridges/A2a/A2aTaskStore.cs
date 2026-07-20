using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace JLEngine.Bridges.A2a;

/// <summary>Port of a2a_server.jl's `_a2a_init_db!`/`_a2a_log_task!`/
/// `_a2a_update_task_snapshot!`/`_a2a_complete_task!`/`_a2a_fail_task!`/
/// `_a2a_get_task` — the a2a_tasks SQLite-backed task log. Push-notification
/// config storage (`a2a_push_notification_configs`) is out of scope for this
/// pass — see the AgentCard/Server class docs for the full scope decision.</summary>
public sealed class A2aTaskStore(SqliteConnection db)
{
    public static void EnsureSchema(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS a2a_tasks (
                id TEXT PRIMARY KEY, created_at TEXT NOT NULL, api_key TEXT,
                input TEXT, tool TEXT, args TEXT, status TEXT DEFAULT 'pending',
                result TEXT, error TEXT, elapsed_ms INTEGER, completed_at TEXT)
            """;
        cmd.ExecuteNonQuery();
    }

    public void LogTask(string id, string apiKey, string input, string tool, object args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO a2a_tasks (id, created_at, api_key, input, tool, args, status)
            VALUES ($id, $now, $key, $input, $tool, $args, 'running')
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$key", apiKey);
        cmd.Parameters.AddWithValue("$input", input);
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$args", JsonSerializer.Serialize(args));
        cmd.ExecuteNonQuery();
    }

    public void CompleteTask(string id, Dictionary<string, object?> resultTask, long elapsedMs)
    {
        UpdateSnapshot(id, resultTask, "TASK_STATE_COMPLETED", "", elapsedMs);
    }

    public void FailTask(string id, string errorMsg, long elapsedMs)
    {
        var task = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["contextId"] = id,
            ["status"] = new Dictionary<string, object?> { ["state"] = "TASK_STATE_FAILED", ["timestamp"] = DateTime.UtcNow.ToString("O") },
            ["history"] = new List<object?>(),
            ["artifacts"] = new List<object?>(),
            ["metadata"] = new Dictionary<string, object?> { ["error"] = errorMsg, ["elapsed_ms"] = elapsedMs },
        };
        UpdateSnapshot(id, task, "TASK_STATE_FAILED", errorMsg, elapsedMs);
    }

    private void UpdateSnapshot(string id, Dictionary<string, object?> task, string status, string errorMsg, long elapsedMs)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE a2a_tasks SET status=$status, result=$result, error=$error, elapsed_ms=$elapsed, completed_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$result", JsonSerializer.Serialize(task));
        cmd.Parameters.AddWithValue("$error", errorMsg);
        cmd.Parameters.AddWithValue("$elapsed", elapsedMs);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, object?>? GetTask(string id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT status, result, error, created_at, completed_at, elapsed_ms FROM a2a_tasks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var resultJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            using var doc = JsonDocument.Parse(resultJson);
            return JLEngine.Core.Config.JsonLoader.Materialize(doc.RootElement) as Dictionary<string, object?>;
        }

        // Task logged but not yet completed — minimal working-state snapshot.
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["contextId"] = id,
            ["status"] = new Dictionary<string, object?> { ["state"] = reader.GetString(0), ["timestamp"] = reader.GetString(3) },
            ["history"] = new List<object?>(),
            ["artifacts"] = new List<object?>(),
            ["metadata"] = new Dictionary<string, object?>(),
        };
    }

    public string? GetTaskState(string id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT status FROM a2a_tasks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    public static bool IsTerminalState(string state) =>
        state.Trim().ToUpperInvariant() is "TASK_STATE_COMPLETED" or "TASK_STATE_FAILED" or "TASK_STATE_CANCELED" or "COMPLETED" or "FAILED" or "CANCELED";
}
