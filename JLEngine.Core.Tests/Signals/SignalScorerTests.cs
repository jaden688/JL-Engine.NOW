using JLEngine.Core.Signals;
using Xunit;

namespace JLEngine.Core.Tests.Signals;

public class SignalScorerTests
{
    [Fact]
    public void HypedMessage_ProducesHighSentimentAndArousal()
    {
        var signals = SignalScorer.Score("This is great! Awesome work, thanks so much! Excellent!");
        Assert.True(signals.Sentiment > 0.5, $"expected high sentiment, got {signals.Sentiment}");
        Assert.True(signals.Arousal > 0.0, $"expected nonzero arousal, got {signals.Arousal}");
    }

    [Fact]
    public void DistressedMessage_ProducesNegativeSentimentAndConfusion()
    {
        var signals = SignalScorer.Score("I'm so confused and frustrated, this is broken, I don't understand what's wrong?");
        Assert.True(signals.Sentiment < 0, $"expected negative sentiment, got {signals.Sentiment}");
        Assert.True(signals.Confusion > 0, $"expected nonzero confusion, got {signals.Confusion}");
    }

    [Fact]
    public void DirectivePhrase_SetsDirectiveTrue()
    {
        var signals = SignalScorer.Score("Just answer, keep it short.");
        Assert.True(signals.Directive);
    }

    [Fact]
    public void NeutralEmptyText_YieldsZeroedSignals()
    {
        var signals = SignalScorer.Score("");
        Assert.Equal(0.0, signals.Sentiment);
        Assert.False(signals.Directive);
    }

    [Fact]
    public void AllValues_AreClampedToUnitRangeWhereExpected()
    {
        var signals = SignalScorer.Score(new string('!', 100) + " confused lost stuck what why help " + new string('?', 50));
        Assert.InRange(signals.Confusion, 0.0, 1.0);
        Assert.InRange(signals.Arousal, 0.0, 1.0);
        Assert.InRange(signals.Pace, 0.0, 1.0);
        Assert.InRange(signals.MemoryDensity, 0.0, 1.0);
        Assert.InRange(signals.Sentiment, -1.0, 1.0);
    }
}
