using JLEngine.Core.Investment;
using JLEngine.Core.Types;
using Xunit;

namespace JLEngine.Core.Tests.Investment;

public class InvestmentSystemTests
{
    [Theory]
    [InlineData(0.1, "low")]
    [InlineData(0.5, "medium")]
    [InlineData(0.7, "high")]
    [InlineData(0.95, "max")]
    public void InvestmentGear_MatchesJuliaTierBoundaries(double level, string expectedGear)
    {
        Assert.Equal(expectedGear, InvestmentSystem.InvestmentGear(level));
    }

    [Fact]
    public void UpdateInvestment_HighArousalAndMemoryDensity_ProducesHighEngagement()
    {
        var system = new InvestmentSystem();
        var signals = new TurnSignals(Sentiment: 0, Arousal: 1.0, Directive: false, Confusion: 0, Pace: 1.0, MemoryDensity: 1.0);
        var level = system.UpdateInvestment(signals, momentum: 1.0, driftPressure: 0.0);

        // engagement = 0.35*1 + 0.25*1 + 0.20*1 + 0.20*1 = 1.0, no drift penalty
        Assert.Equal(1.0, level, precision: 10);
        Assert.Equal(level, system.Level);
        Assert.Equal(level, system.InvestmentTarget());
    }

    [Fact]
    public void UpdateInvestment_DriftPressureReducesLevel()
    {
        var system = new InvestmentSystem();
        var signals = new TurnSignals(Sentiment: 0, Arousal: 1.0, Directive: false, Confusion: 0, Pace: 1.0, MemoryDensity: 1.0);
        var level = system.UpdateInvestment(signals, momentum: 1.0, driftPressure: 1.0);

        // pressure_penalty = clamp(1.0*0.15, 0, 0.35) = 0.15 -> level = 1.0 - 0.15 = 0.85
        Assert.Equal(0.85, level, precision: 10);
    }

    [Fact]
    public void GearLabel_IsNowRecognizedByGearModifiers_Phase5Fix()
    {
        // Phase 5 fix: Gear.Modifiers gained a second tier table for
        // Investment's "low"/"medium"/"high"/"max" labels, so each investment
        // level now resolves to its own distinct, non-default modifier set —
        // fixing the Phase 1-documented vocabulary mismatch.
        var levels = new[] { 0.1, 0.5, 0.7, 0.95 };
        var seenModifiers = new HashSet<JLEngine.Core.Types.GearModifiers>();

        foreach (var level in levels)
        {
            var gear = InvestmentSystem.InvestmentGear(level);
            var modifiers = JLEngine.Core.Types.Gear.Modifiers(gear);
            Assert.NotEqual(new JLEngine.Core.Types.GearModifiers(0.8, 0.65, false), modifiers);
            seenModifiers.Add(modifiers);
        }

        Assert.Equal(levels.Length, seenModifiers.Count); // all 4 tiers must be distinct
    }

    [Fact]
    public void GearModifiers_ReactionSpeedIncreasesMonotonically_LowToMax()
    {
        // Higher investment should react to signals faster (lower inertia),
        // matching the worm->planetary spectrum's existing direction.
        var low = JLEngine.Core.Types.Gear.Modifiers("low");
        var medium = JLEngine.Core.Types.Gear.Modifiers("medium");
        var high = JLEngine.Core.Types.Gear.Modifiers("high");
        var max = JLEngine.Core.Types.Gear.Modifiers("max");

        Assert.True(low.ReactionSpeed < medium.ReactionSpeed);
        Assert.True(medium.ReactionSpeed < high.ReactionSpeed);
        Assert.True(high.ReactionSpeed < max.ReactionSpeed);

        Assert.True(low.ModeInertia > medium.ModeInertia);
        Assert.True(medium.ModeInertia > high.ModeInertia);
        Assert.True(high.ModeInertia > max.ModeInertia);
    }
}
