using JLEngine.Core.Drift;
using JLEngine.Core.Types;
using Xunit;

namespace JLEngine.Core.Tests.Drift;

public class DriftPressureSystemTests
{
    [Fact]
    public void PerfectAlignment_ProducesZeroPressure()
    {
        var pressure = DriftPressureSystem.Calculate(new DriftPressureInput());
        Assert.Equal(0.0, pressure, precision: 10);
    }

    [Fact]
    public void ZeroAlignment_ProducesMaximalPressure()
    {
        var input = new DriftPressureInput
        {
            AgentAlignmentScore = 0.0,
            BehaviorGridAlignmentScore = 0.0,
            SafetyAlignmentScore = 0.0,
            MemoryAlignmentScore = 0.0,
            ConversationalCoherenceScore = 0.0,
        };
        var pressure = DriftPressureSystem.Calculate(input);
        Assert.Equal(1.0, pressure, precision: 10);
    }

    [Theory]
    [InlineData(0.05, "Nominal", 0.0)]
    [InlineData(0.30, "Soft Drift", -0.05)]
    [InlineData(0.60, "Moderate Drift", -0.10)]
    [InlineData(0.90, "Hard Drift", -0.20)]
    public void ResponseTiers_MatchJuliaThresholds(double pressure, string expectedLevel, double expectedDelta)
    {
        var response = DriftPressureSystem.GetResponseAction(pressure);
        Assert.Equal(expectedLevel, response.ActionLevel);
        Assert.Equal(expectedDelta, response.TemperatureDelta);
    }

    [Fact]
    public void HardDrift_PopulatesForceFields_EvenThoughUnconsumedElsewhere()
    {
        var response = DriftPressureSystem.GetResponseAction(0.9);
        Assert.Equal("lockstep", response.ForceGait);
        Assert.Equal("strict", response.ForceRhythm);
        Assert.NotNull(response.SupervisorWarning);
    }

    [Fact]
    public void SoftDrift_IsTheOnlyTierWithReinforceGait()
    {
        Assert.True(DriftPressureSystem.GetResponseAction(0.30).ReinforceGait);
        Assert.False(DriftPressureSystem.GetResponseAction(0.05).ReinforceGait);
        Assert.False(DriftPressureSystem.GetResponseAction(0.60).ReinforceGait);
        Assert.False(DriftPressureSystem.GetResponseAction(0.90).ReinforceGait);
    }
}
