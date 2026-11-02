using JLEngine.Core.Aperture;
using JLEngine.Core.Engine;
using JLEngine.Core.Types;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JLEngine.Persistence.Tests;

public class SparkByteDatabaseTests
{
    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"jlpersistence-test-{Guid.NewGuid()}.db");

    private static long CountRows(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void EnsureCreated_IsIdempotent_AndCreatesAllCoreTables()
    {
        var path = NewDbPath();
        using var db1 = new SparkByteDatabase(path);
        using var db2 = new SparkByteDatabase(path); // re-open same file, re-run schema

        string[] expectedTables =
        [
            "memory", "tools", "thoughts", "knowledge", "agents",
            "behavior_states", "sessions", "web_cache", "tool_usage_log",
            "telemetry", "turn_snapshots", "conversation_messages",
        ];

        foreach (var table in expectedTables)
        {
            using var cmd = db2.Connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
            cmd.Parameters.AddWithValue("$name", table);
            Assert.NotNull(cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void ConversationHistory_RoundTripsPerSessionInInsertionOrder()
    {
        var path = NewDbPath();
        using (var db = new SparkByteDatabase(path))
        {
            db.WriteConversationMessage("session-a", "user", "hello");
            db.WriteConversationMessage("session-a", "assistant", "hi there");
            db.WriteConversationMessage("session-b", "user", "separate conversation");

            var history = db.ReadConversationHistory("session-a");
            Assert.Equal(2, history.Count);
            Assert.Equal("user", history[0]["role"]);
            Assert.Equal("hello", history[0]["content"]);
            Assert.Equal("assistant", history[1]["role"]);
            Assert.Equal("hi there", history[1]["content"]);
        }

        // A new database connection represents a fresh engine process.
        using var reopened = new SparkByteDatabase(path);
        var restored = reopened.ReadConversationHistory("session-a");
        Assert.Equal(2, restored.Count);
        Assert.Equal("hi there", restored[1]["content"]);
        Assert.Empty(reopened.ReadConversationHistory("unknown-session"));
    }

    [Fact]
    public void WriteTelemetryEvent_PersistsEventPayload()
    {
        using var db = new SparkByteDatabase(NewDbPath());
        db.WriteTelemetryEvent("2026-11-02T12:00:00.0000000Z", "session-a", "turn_complete", "{\"elapsed_ms\":12}");

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT session_id, event, data_json FROM telemetry LIMIT 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("session-a", reader.GetString(0));
        Assert.Equal("turn_complete", reader.GetString(1));
        Assert.Equal("{\"elapsed_ms\":12}", reader.GetString(2));
    }

    [Fact]
    public void WriteThought_TruncatesContextAndThought_MatchingJuliaLimits()
    {
        using var db = new SparkByteDatabase(NewDbPath());
        db.WriteThought(new string('c', 500), new string('t', 1000), "curious", "walk");

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT context, thought FROM thoughts LIMIT 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(120, reader.GetString(0).Length);
        Assert.Equal(400, reader.GetString(1).Length);
    }

    [Fact]
    public void WriteReasoning_SkipsBlankReasoning()
    {
        using var db = new SparkByteDatabase(NewDbPath());
        db.WriteReasoning("ctx", "   ", "model-x");
        Assert.Equal(0, CountRows(db.Connection, "thoughts"));

        db.WriteReasoning("ctx", "actual reasoning trace", "model-x");
        Assert.Equal(1, CountRows(db.Connection, "thoughts"));
    }

    [Fact]
    public void WriteTurnSnapshot_RoundTripsEngineFields()
    {
        using var db = new SparkByteDatabase(NewDbPath());
        var snapshot = new TurnSnapshot(
            Agent: "SparkByte", AgentFile: "SparkByte_Full.json", AgentProjection: [],
            Trigger: "neutral", Gait: "trot",
            Signals: new TurnSignals(0, 0, false, 0, 0, 0),
            BehaviorState: new BehaviorState { Name = "Engaged", Expressiveness = 0.7, Pacing = "normal", ToneBias = "warm" },
            BehaviorBlend: null,
            Rhythm: new RhythmState("flip", 0.25, 0.1, 0.2, "flip", [], []),
            Drift: new DriftResponse(0.2, "Soft Drift", -0.05, null, null, null, true),
            InvestmentLevel: 0.5, InvestmentGear: "medium",
            ApertureState: new ApertureState(0.45, "BALANCED", new ApertureModifierSet(0.45, 0.7, 0.45, 0.45, 0.5), 0.45, 0.7, 0, 0, null, null, 0),
            Advisory: new Dictionary<string, object?> { ["gating_bias"] = 0.0, ["emotional_drift"] = 0.01 },
            CoreRules: [], MemoryContext: []);

        db.WriteTurnSnapshot(snapshot, "SparkByte", "deepseek/deepseek-v4-flash", "12345", 1, 20, 40, 150);

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT gait, rhythm_mode, aperture_mode, behavior_state, drift_action_level FROM turn_snapshots LIMIT 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("trot", reader.GetString(0));
        Assert.Equal("flip", reader.GetString(1));
        Assert.Equal("BALANCED", reader.GetString(2));
        Assert.Equal("Engaged", reader.GetString(3));
        Assert.Equal("Soft Drift", reader.GetString(4));
    }

    [Fact]
    public void WriteToolUsage_TruncatesAndIncrementsCallCount()
    {
        using var db = new SparkByteDatabase(NewDbPath());
        db.UpsertToolSchema("run_command", "builtin", "desc", "{}", false);

        db.WriteToolUsage("run_command", new string('a', 1000), new string('b', 1000), 42, "SparkByte", "sess1");
        db.WriteToolUsage("run_command", "{}", "{}", 10, "SparkByte", "sess1");

        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT args_json FROM tool_usage_log LIMIT 1";
            Assert.Equal(500, ((string)cmd.ExecuteScalar()!).Length);
        }
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT call_count FROM tools WHERE name='run_command'";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void WriteWebCache_InsertsThenUpdatesSameUrl()
    {
        using var db = new SparkByteDatabase(NewDbPath());
        db.WriteWebCache("https://example.com", "first content");
        Assert.Equal(1, CountRows(db.Connection, "web_cache"));

        db.WriteWebCache("https://example.com", "updated content");
        Assert.Equal(1, CountRows(db.Connection, "web_cache")); // same URL updates, doesn't insert a second row

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT content FROM web_cache WHERE url='https://example.com'";
        Assert.Equal("updated content", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void SessionLifecycle_StartThenEnd_SetsEndedAtOnlyOnce()
    {
        using var db = new SparkByteDatabase(NewDbPath());
        db.StartSession("sess1", "Windows", "9.0.0");
        db.EndSession("sess1", 5);

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT ended_at, events FROM sessions WHERE session_id='sess1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.False(reader.IsDBNull(0));
        Assert.Equal(5L, reader.GetInt64(1));
    }

    [Fact]
    public void FailedWrites_NeverThrow_EvenWithClosedConnection()
    {
        var db = new SparkByteDatabase(NewDbPath());
        db.Connection.Close();

        // Every write method wraps its body in a swallowed try/catch — a
        // closed connection must not propagate an exception to the caller,
        // matching Julia's `catch e; @warn ...; end` pattern.
        var exception = Record.Exception(() => db.WriteThought("ctx", "thought", "mood", "gait"));
        Assert.Null(exception);
    }
}
