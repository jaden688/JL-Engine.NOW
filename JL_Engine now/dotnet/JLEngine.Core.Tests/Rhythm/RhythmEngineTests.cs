using JLEngine.Core.Rhythm;
using Xunit;

namespace JLEngine.Core.Tests.Rhythm;

public class RhythmEngineTests
{
    [Theory]
    [InlineData("flip", "flip")]
    [InlineData("FLOP", "flop")]
    [InlineData("Trot", "trot")]
    [InlineData("twitch", "trot")]
    [InlineData("burst", "trot")]
    [InlineData("cascade", "flip")]
    [InlineData("stutter", "flop")]
    [InlineData("unknown-garbage", "flip")]
    public void NormalizeMode_MatchesJuliaAliasTable(string input, string expected)
    {
        Assert.Equal(expected, RhythmEngine.NormalizeMode(input));
    }

    [Fact]
    public void DistressedTrigger_ForcesFlop_EvenIfBaseTriggerWouldBeTrot()
    {
        var engine = new RhythmEngine();
        var state = engine.Compute(lastMode: "flip", trigger: "user_hyped", gait: "sprint", safetyOn: true);
        // user_hyped + sprint gait would normally push toward trot, but this
        // test targets the safety-rule path specifically:
        var distressed = engine.Compute(lastMode: state.Mode, trigger: "user_distressed", gait: "walk", safetyOn: true);
        Assert.Equal("flop", distressed.Mode);
    }

    [Fact]
    public void SafetyRules_Skipped_WhenSafetyOff()
    {
        var engine = new RhythmEngine();
        // Force momentum toward trot first, then check that user_distressed no
        // longer forces flop when safety_on=false — the safety-rule branch
        // becomes a pure normalize-only pass-through.
        var state = engine.Compute(lastMode: "trot", trigger: "user_distressed", gait: "trot", safetyOn: false);
        Assert.Equal("trot", state.Mode);
    }

    [Fact]
    public void HighDriftPressure_ForcesFlopRegardlessOfTrigger()
    {
        var engine = new RhythmEngine();
        var state = engine.Compute(lastMode: "flip", trigger: "user_hyped", gait: "walk", driftPressure: 0.8, safetyOn: false);
        Assert.Equal("flop", state.Mode);
    }
}
