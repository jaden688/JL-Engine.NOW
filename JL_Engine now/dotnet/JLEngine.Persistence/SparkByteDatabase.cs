using JLEngine.Core.Engine;
using Microsoft.Data.Sqlite;

namespace JLEngine.Persistence;

/// <summary>
/// Port of Tools.jl's `_db_write_*` / `_db_start_session` / `_db_end_session`
/// family (Tools.jl:40-170). Every write is wrapped to never throw past this
/// class — matching Julia's `catch e; @warn ...; end` pattern, since these
/// are side-channel persistence writes that must never break the caller's
/// actual turn/tool-call.
/// </summary>
public sealed class SparkByteDatabase : IDisposable
{
    public SqliteConnection Connection { get; }

    public SparkByteDatabase(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        Connection = new SqliteConnection($"Data Source={dbPath}");
        Connection.Open();
        SparkByteSchema.EnsureCreated(Connection);
    }

    private static string Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : text[..max];
    }

    public void WriteThought(string context, string thought, string mood, string gait, string agent = "SparkByte", string type = "diary", string model = "")
    {
        Try(() =>
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO thoughts (timestamp, agent, context, thought, mood, gait, type, model) VALUES ($ts,$agent,$ctx,$thought,$mood,$gait,$type,$model)";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$agent", agent);
            cmd.Parameters.AddWithValue("$ctx", Truncate(context, 120));
            cmd.Parameters.AddWithValue("$thought", Truncate(thought, 400));
            cmd.Parameters.AddWithValue("$mood", mood);
            cmd.Parameters.AddWithValue("$gait", gait);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Port of `_db_write_reasoning`: stores raw reasoning/thinking traces
    /// from reasoning models. No-op on blank reasoning, matching Julia.</summary>
    public void WriteReasoning(string context, string reasoning, string model, string agent = "SparkByte")
    {
        if (string.IsNullOrWhiteSpace(reasoning)) return;
        Try(() =>
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO thoughts (timestamp, agent, context, thought, mood, gait, type, model) VALUES ($ts,$agent,$ctx,$thought,'reasoning','auto','reasoning',$model)";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$agent", agent);
            cmd.Parameters.AddWithValue("$ctx", Truncate(context, 120));
            cmd.Parameters.AddWithValue("$thought", Truncate(reasoning, 2000));
            cmd.Parameters.AddWithValue("$model", model);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Port of `_db_write_turn_snapshot`, taking the strongly-typed
    /// TurnSnapshot directly (Phase 1's typed record) instead of a raw dict.</summary>
    public void WriteTurnSnapshot(TurnSnapshot snapshot, string agent, string model, string sessionId, int turnNumber, int userMsgLen, int replyLen, long elapsedMs)
    {
        Try(() =>
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO turn_snapshots
                (timestamp, session_id, turn_number, agent, model,
                 gait, rhythm_mode, rhythm_momentum,
                 aperture_mode, aperture_temp, aperture_top_p,
                 behavior_state, behavior_expressiveness, behavior_pacing, behavior_tone,
                 drift_pressure, drift_temp_delta, drift_action_level,
                 advisory_bias, advisory_emotional_drift, advisory_msg,
                 user_msg_len, reply_len, elapsed_ms)
                VALUES ($ts,$sid,$turn,$agent,$model,
                        $gait,$rmode,$rmom,
                        $amode,$atemp,$atop,
                        $bstate,$bexpr,$bpacing,$btone,
                        $dpress,$dtemp,$daction,
                        $abias,$adrift,$amsg,
                        $ulen,$rlen,$elapsed)
                """;
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$turn", turnNumber);
            cmd.Parameters.AddWithValue("$agent", agent);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.Parameters.AddWithValue("$gait", snapshot.Gait);
            cmd.Parameters.AddWithValue("$rmode", snapshot.Rhythm.Mode);
            cmd.Parameters.AddWithValue("$rmom", snapshot.Rhythm.Momentum);
            cmd.Parameters.AddWithValue("$amode", snapshot.ApertureState.Mode);
            cmd.Parameters.AddWithValue("$atemp", snapshot.ApertureState.Temp);
            cmd.Parameters.AddWithValue("$atop", snapshot.ApertureState.TopP);
            cmd.Parameters.AddWithValue("$bstate", snapshot.BehaviorState.Name);
            cmd.Parameters.AddWithValue("$bexpr", snapshot.BehaviorState.Expressiveness);
            cmd.Parameters.AddWithValue("$bpacing", snapshot.BehaviorState.Pacing);
            cmd.Parameters.AddWithValue("$btone", snapshot.BehaviorState.ToneBias);
            cmd.Parameters.AddWithValue("$dpress", snapshot.Drift.Pressure);
            cmd.Parameters.AddWithValue("$dtemp", snapshot.Drift.TemperatureDelta);
            cmd.Parameters.AddWithValue("$daction", snapshot.Drift.ActionLevel);
            cmd.Parameters.AddWithValue("$abias", snapshot.Advisory.GetValueOrDefault("gating_bias")?.ToString() ?? "");
            cmd.Parameters.AddWithValue("$adrift", snapshot.Advisory.GetValueOrDefault("emotional_drift")?.ToString() ?? "");
            cmd.Parameters.AddWithValue("$amsg", "");
            cmd.Parameters.AddWithValue("$ulen", userMsgLen);
            cmd.Parameters.AddWithValue("$rlen", replyLen);
            cmd.Parameters.AddWithValue("$elapsed", elapsedMs);
            cmd.ExecuteNonQuery();
        });
    }

    public void WriteToolUsage(string name, string argsJson, string resultJson, long elapsedMs, string agent, string sessionId)
    {
        Try(() =>
        {
            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO tool_usage_log (timestamp, tool_name, args_json, result_json, duration_ms, agent, session_id) VALUES ($ts,$name,$args,$result,$dur,$agent,$sid)";
                cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$args", Truncate(argsJson, 500));
                cmd.Parameters.AddWithValue("$result", Truncate(resultJson, 500));
                cmd.Parameters.AddWithValue("$dur", elapsedMs);
                cmd.Parameters.AddWithValue("$agent", agent);
                cmd.Parameters.AddWithValue("$sid", sessionId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE tools SET call_count = call_count + 1, last_used = $ts WHERE name = $name";
                cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$name", name);
                cmd.ExecuteNonQuery();
            }
        });
    }

    public void WriteWebCache(string url, string content)
    {
        Try(() =>
        {
            long existingId = -1;
            using (var checkCmd = Connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT id FROM web_cache WHERE url = $url";
                checkCmd.Parameters.AddWithValue("$url", url);
                var result = checkCmd.ExecuteScalar();
                if (result is not null) existingId = Convert.ToInt64(result);
            }

            var summary = Truncate(content, 300);
            using var cmd = Connection.CreateCommand();
            if (existingId < 0)
            {
                cmd.CommandText = "INSERT INTO web_cache (url, fetched_at, content, summary, tags) VALUES ($url,$ts,$content,$summary,'browsed')";
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$content", Truncate(content, 5000));
                cmd.Parameters.AddWithValue("$summary", summary);
            }
            else
            {
                cmd.CommandText = "UPDATE web_cache SET fetched_at=$ts, content=$content, summary=$summary WHERE url=$url";
                cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$content", Truncate(content, 5000));
                cmd.Parameters.AddWithValue("$summary", summary);
                cmd.Parameters.AddWithValue("$url", url);
            }
            cmd.ExecuteNonQuery();
        });
    }

    public void StartSession(string sessionId, string os, string runtimeVersion)
    {
        Try(() =>
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (session_id, started_at, os, julia_ver, events, notes) VALUES ($sid,$ts,$os,$ver,0,'Boot')";
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$os", os);
            cmd.Parameters.AddWithValue("$ver", runtimeVersion);
            cmd.ExecuteNonQuery();
        });
    }

    public void EndSession(string sessionId, int eventCount)
    {
        Try(() =>
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET ended_at=$ts, events=$count WHERE session_id=$sid AND ended_at IS NULL";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$count", eventCount);
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Port of `init_tools`'s tools-table sync: upserts one row per
    /// declared tool schema, preserving any existing call_count.</summary>
    public void UpsertToolSchema(string name, string source, string description, string parametersJson, bool isDynamic)
    {
        Try(() =>
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO tools (name, source, description, parameters, is_dynamic, call_count)
                VALUES ($name, $source, $desc, $params, $dyn, COALESCE((SELECT call_count FROM tools WHERE name = $name), 0))
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$desc", description);
            cmd.Parameters.AddWithValue("$params", parametersJson);
            cmd.Parameters.AddWithValue("$dyn", isDynamic ? 1 : 0);
            cmd.ExecuteNonQuery();
        });
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Persistence writes must never break the caller's actual turn/tool-call.
        }
    }

    public void Dispose() => Connection.Dispose();
}
