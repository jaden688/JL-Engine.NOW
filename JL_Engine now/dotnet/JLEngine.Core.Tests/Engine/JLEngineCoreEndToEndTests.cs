using JLEngine.Core.Engine;
using JLEngine.Core.Types;
using Xunit;

namespace JLEngine.Core.Tests.Engine;

public class JLEngineCoreEndToEndTests
{
    [Fact]
    public async Task ScriptedConversation_RunsEndToEnd_AgainstNoopBackend_WithoutApiKey()
    {
        // Uses a RootDir pointing nowhere real, so every JSON config load
        // safely falls back to defaults (matching Julia's load_json_safely) —
        // this is purely wiring verification, not agent-card fidelity.
        var config = new EngineConfig { RootDir = Path.Combine(Path.GetTempPath(), $"jlengine-test-{Guid.NewGuid()}") };
        var engine = new JLEngineCore(config);

        string[] turns =
        [
            "Hey, how's it going?",
            "This is great, thanks so much! Awesome!",
            "I'm confused and frustrated, this isn't working, why?",
            "Just answer, keep it short please.",
        ];

        foreach (var turn in turns)
        {
            var result = await engine.RunTurnAsync(turn, backendId: "noop-stub");
            Assert.True((bool)result["ok"]!);
            Assert.False(string.IsNullOrEmpty(result["reply"] as string));
        }

        // NoopBackend echoes the user's last message, so the reply should
        // equal the final turn's text.
        var last = await engine.RunTurnAsync("final check", backendId: "noop-stub");
        Assert.Equal("final check", last["reply"]);
    }

    [Fact]
    public void AnalyzeTurn_ThenRunTurn_ProduceConsistentSnapshotFields()
    {
        var config = new EngineConfig { RootDir = Path.Combine(Path.GetTempPath(), $"jlengine-test-{Guid.NewGuid()}") };
        var engine = new JLEngineCore(config);

        var snapshot = engine.AnalyzeTurn("This is great, awesome, thanks!");
        Assert.Equal("neutral", snapshot.Trigger); // sentiment>0.5 alone isn't enough; needs arousal>0.5 too for user_hyped
        Assert.InRange(snapshot.ApertureState.Score, 0.0, 1.0);
        Assert.Contains(snapshot.ApertureState.Mode, new[] { "CLOSED", "GUARDED", "BALANCED", "OPEN", "WIDE_OPEN" });

        var context = engine.RecordTurn("This is great, awesome, thanks!", "noop reply", snapshot);
        Assert.NotNull(context);
        Assert.True(context.ContainsKey("agent_memory"));
    }

    [Fact]
    public void SetAgent_UnknownName_FallsBackToDefaultAgent()
    {
        var config = new EngineConfig { RootDir = Path.Combine(Path.GetTempPath(), $"jlengine-test-{Guid.NewGuid()}") };
        var engine = new JLEngineCore(config);

        // With no MPF registry file present, MpfProfiles is empty, so SetAgent
        // always returns false (no profile to select) — mirrors Julia exactly.
        var result = engine.SetAgent("SomeAgentThatDoesNotExist");
        Assert.False(result);
    }
}
