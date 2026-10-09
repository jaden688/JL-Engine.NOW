using Microsoft.Data.Sqlite;

namespace JLEngine.Persistence;

/// <summary>
/// Port of `_open_memory_db`'s CREATE TABLE statements (src/App.jl:60-116)
/// and the index list (src/App.jl:291-304). Exact column-for-column match
/// with the live sparkbyte_memory.db schema confirmed via direct sqlite3
/// inspection. Covers the 10 core tables; the 4 A2A-specific tables
/// (a2a_tasks, a2a_push_notification_configs, a2a_accounts, a2a_usage_ledger)
/// are deferred to Phase 4 along with the A2A server itself.
///
/// No EF Core migrations — idempotent CREATE TABLE/INDEX IF NOT EXISTS only,
/// matching Julia's own lack of a formal migration system.
/// </summary>
public static class SparkByteSchema
{
    private static readonly string[] CreateTableStatements =
    [
        "CREATE TABLE IF NOT EXISTS memory (id INTEGER PRIMARY KEY, timestamp TEXT, tag TEXT, key TEXT, content TEXT)",

        """
        CREATE TABLE IF NOT EXISTS tools (
            id INTEGER PRIMARY KEY, name TEXT UNIQUE, source TEXT, description TEXT,
            parameters TEXT, is_dynamic INTEGER DEFAULT 0, forged_at TEXT, last_used TEXT, call_count INTEGER DEFAULT 0)
        """,

        """
        CREATE TABLE IF NOT EXISTS thoughts (
            id INTEGER PRIMARY KEY, timestamp TEXT, agent TEXT DEFAULT 'SparkByte',
            context TEXT, thought TEXT, mood TEXT, gait TEXT,
            type TEXT DEFAULT 'diary', model TEXT DEFAULT '')
        """,

        """
        CREATE TABLE IF NOT EXISTS knowledge (
            id INTEGER PRIMARY KEY, domain TEXT, topic TEXT, content TEXT, source TEXT, learned TEXT)
        """,

        """
        CREATE TABLE IF NOT EXISTS agents (
            id INTEGER PRIMARY KEY, name TEXT UNIQUE, description TEXT, personality TEXT,
            tone TEXT, boot_prompt TEXT, active INTEGER DEFAULT 0, last_used TEXT)
        """,

        """
        CREATE TABLE IF NOT EXISTS behavior_states (
            id INTEGER PRIMARY KEY, state_id TEXT UNIQUE, name TEXT, intensity INTEGER, control INTEGER,
            expressiveness REAL, pacing TEXT, tone_bias TEXT, memory_strictness TEXT, trigger_conditions TEXT)
        """,

        """
        CREATE TABLE IF NOT EXISTS sessions (
            id INTEGER PRIMARY KEY, session_id TEXT, started_at TEXT, ended_at TEXT,
            os TEXT, julia_ver TEXT, events INTEGER DEFAULT 0, notes TEXT)
        """,

        """
        CREATE TABLE IF NOT EXISTS web_cache (
            id INTEGER PRIMARY KEY, url TEXT, fetched_at TEXT, content TEXT, summary TEXT, tags TEXT)
        """,

        """
        CREATE TABLE IF NOT EXISTS tool_usage_log (
            id INTEGER PRIMARY KEY, timestamp TEXT, tool_name TEXT, args_json TEXT,
            result_json TEXT, duration_ms INTEGER, agent TEXT, session_id TEXT)
        """,

        """
        CREATE TABLE IF NOT EXISTS telemetry (
            id INTEGER PRIMARY KEY, timestamp TEXT, session_id TEXT, event TEXT,
            turn_number INTEGER DEFAULT 0, model TEXT DEFAULT '', agent TEXT DEFAULT '',
            data_json TEXT)
        """,

        """
        CREATE TABLE IF NOT EXISTS turn_snapshots (
            id INTEGER PRIMARY KEY,
            timestamp TEXT,
            session_id TEXT,
            turn_number INTEGER,
            agent TEXT,
            model TEXT,
            gait TEXT,
            rhythm_mode TEXT,
            rhythm_momentum REAL,
            aperture_mode TEXT,
            aperture_temp REAL,
            aperture_top_p REAL,
            behavior_state TEXT,
            behavior_expressiveness REAL,
            behavior_pacing TEXT,
            behavior_tone TEXT,
            drift_pressure REAL,
            drift_temp_delta REAL,
            drift_action_level TEXT,
            advisory_bias TEXT,
            advisory_emotional_drift TEXT,
            advisory_msg TEXT,
            user_msg_len INTEGER,
            reply_len INTEGER,
            elapsed_ms INTEGER)
        """,
    ];

    private static readonly string[] CreateIndexStatements =
    [
        "CREATE INDEX IF NOT EXISTS idx_memory_tag ON memory(tag)",
        "CREATE INDEX IF NOT EXISTS idx_knowledge_domain ON knowledge(domain)",
        "CREATE INDEX IF NOT EXISTS idx_knowledge_topic ON knowledge(domain, topic)",
        "CREATE INDEX IF NOT EXISTS idx_behavior_name ON behavior_states(name)",
        "CREATE INDEX IF NOT EXISTS idx_agents_name ON agents(name)",
        "CREATE INDEX IF NOT EXISTS idx_telemetry_event ON telemetry(event)",
        "CREATE INDEX IF NOT EXISTS idx_telemetry_agent ON telemetry(agent)",
        "CREATE INDEX IF NOT EXISTS idx_thoughts_type ON thoughts(type)",
        "CREATE INDEX IF NOT EXISTS idx_thoughts_agent ON thoughts(agent)",
        "CREATE INDEX IF NOT EXISTS idx_tool_usage_name ON tool_usage_log(tool_name)",
    ];

    public static void EnsureCreated(SqliteConnection db)
    {
        foreach (var sql in CreateTableStatements)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        foreach (var sql in CreateIndexStatements)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
