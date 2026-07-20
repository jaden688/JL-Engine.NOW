using JLEngine.Core.Aperture;
using JLEngine.Core.Types;
using Xunit;

namespace JLEngine.Core.Tests.Aperture;

public class EmotionalApertureTests
{
    [Fact]
    public void SafetyCeiling_NeverExceeds060_EvenWithMaximalInputs()
    {
        var aperture = new EmotionalAperture();
        var maxedBehavior = new BehaviorState { Expressiveness = 1.0 };

        ApertureState? state = null;
        // Drive several turns with every input pushed to maximize openness,
        // so blending/momentum has every chance to push the score past the
        // ceiling if the clamp weren't re-applied post-blend each time.
        for (var i = 0; i < 10; i++)
        {
            state = aperture.UpdateFromSignals(
                behaviorState: maxedBehavior,
                gait: "sprint",
                rhythm: "burst",
                agentVividness: 1.0,
                safetyMode: true,
                driftPressure: 0.0,
                driftBiasIn: 1.0,
                userSentiment: 1.0,
                conversationPacing: 1.0,
                memoryDensity: 1.0,
                apertureBias: 1.0);

            Assert.True(state.Score <= 0.60, $"turn {i}: score {state.Score} exceeded the 0.60 safety ceiling");
        }
    }

    [Fact]
    public void SafetyCeiling_DoesNotApply_WhenSafetyModeOff()
    {
        var aperture = new EmotionalAperture();
        var maxedBehavior = new BehaviorState { Expressiveness = 1.0 };

        ApertureState state = null!;
        for (var i = 0; i < 10; i++)
        {
            state = aperture.UpdateFromSignals(
                behaviorState: maxedBehavior,
                gait: "sprint",
                rhythm: "burst",
                agentVividness: 1.0,
                safetyMode: false,
                driftPressure: 0.0,
                driftBiasIn: 1.0,
                userSentiment: 1.0,
                conversationPacing: 1.0,
                memoryDensity: 1.0,
                apertureBias: 1.0);
        }

        // With safety off and maximal inputs sustained over several turns,
        // the score should be able to climb above the 0.60 ceiling.
        Assert.True(state.Score > 0.60, $"expected score above 0.60 with safety off, got {state.Score}");
    }

    [Fact]
    public void ModeFromScore_BoundariesMatchJulia()
    {
        var aperture = new EmotionalAperture();

        // Neutral/default inputs should land in a low-openness mode by default
        // (BALANCED or below) given the -drift/+minimal signal starting point.
        var state = aperture.UpdateFromSignals(behaviorState: null, safetyMode: false);
        Assert.Contains(state.Mode, new[] { "CLOSED", "GUARDED", "BALANCED" });
    }

    [Fact]
    public void Reset_ReturnsToGuardedBaseline()
    {
        var aperture = new EmotionalAperture();
        aperture.UpdateFromSignals(behaviorState: new BehaviorState { Expressiveness = 1.0 }, safetyMode: false, userSentiment: 1.0);
        aperture.Reset();

        var state = aperture.GetState();
        Assert.Equal("GUARDED", state.Mode);
        Assert.Equal(0.25, state.Score);
        Assert.Equal(0.0, aperture.GetFocusLevel());
        Assert.Equal(0.0, aperture.GetOverloadLevel());
    }
}
