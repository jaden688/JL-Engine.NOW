using System.Text.RegularExpressions;
using JLEngine.Core.Types;

namespace JLEngine.Core.Signals;

/// <summary>
/// Port of Signals.jl. Deterministic bag-of-words heuristics, no ML.
/// Faithful port: includes the dead `upper_hits` variable (hardcoded to 0
/// in the Julia source, so the "shouting/caps" signal is never actually
/// computed despite the branch existing) per the faithful-port decision.
/// </summary>
public static partial class SignalScorer
{
    private static readonly HashSet<string> PosWords =
    [
        "great", "awesome", "thanks", "good", "fantastic", "excellent", "happy", "joy",
        "wonderful", "brilliant", "support", "clarity", "help", "solve", "guide", "create",
        "build", "innovate", "progress", "success", "win", "improve", "calm", "relaxed",
        "relief", "confident", "thankful", "appreciate", "grateful", "team", "collaborate", "ally",
        "energized", "motivated", "inspired", "bright", "spark", "positive", "optimistic",
        "steady", "resilient", "glad", "hopeful", "focus", "clarify", "achieve", "resolve",
        "empower", "assist",
    ];

    private static readonly HashSet<string> NegWords =
    [
        "bad", "hate", "angry", "annoyed", "frustrated", "upset", "broken", "issue",
        "problem", "confused", "lost", "stuck", "sad", "terrible", "awful", "worst",
        "fail", "error", "panic", "worry", "anxiety", "fear", "hurt", "tired",
        "exhausted", "depressed", "miserable", "scared", "danger", "crash", "stop",
        "delay", "weak", "stress", "tension", "dread", "overwhelmed", "rude",
        "hostile", "suck",
    ];

    private static readonly string[] DirectivePhrases =
    [
        "be concise", "just answer", "short answer", "no fluff", "get to the point",
        "bullet points", "keep it short", "fast summary", "direct answer",
        "only the essentials", "tell me the facts", "focus", "minimal words",
        "skip the fluff", "straight answer", "rapid response",
    ];

    private static readonly HashSet<string> ConfuseWords =
    [
        "confused", "lost", "stuck", "don't", "get", "not", "sure", "unclear",
        "huh", "what", "why", "help",
    ];

    [GeneratedRegex("[a-z']+")]
    private static partial Regex WordPattern();

    private static double ClampUnit(double value) => Math.Max(0.0, Math.Min(1.0, value));

    public static TurnSignals Score(string text)
    {
        var lowered = text.ToLowerInvariant();
        var words = WordPattern().Matches(lowered).Select(m => m.Value).ToList();
        var wlen = words.Count;

        var posHits = words.Count(PosWords.Contains);
        var negHits = words.Count(NegWords.Contains);
        var sentiment = (posHits - negHits) / (double)Math.Max(1, wlen);
        sentiment = Math.Max(-1.0, Math.Min(1.0, sentiment * 6.0));

        var directive = DirectivePhrases.Any(lowered.Contains);
        var confusionHits = words.Count(ConfuseWords.Contains) + lowered.Count(c => c == '?');
        var confusion = ClampUnit(confusionHits / (double)Math.Max(3, wlen));

        var exclaim = lowered.Count(c => c == '!');
        const int upperHits = 0; // dead in the Julia source: "shouting" signal is never actually computed
        var arousal = wlen * 0.04 + (exclaim > 0 ? 0.25 : 0.0) + Math.Max(0, exclaim - 1) * 0.05 + (upperHits > 0 ? 0.2 : 0.0);
        arousal = ClampUnit(arousal);

        var pace = Math.Min(wlen, 30) / 30.0 + (exclaim > 0 ? 0.10 : 0.0);
        pace = ClampUnit(pace);

        var memoryDensity = wlen / 35.0 + confusionHits * 0.08;
        memoryDensity = ClampUnit(memoryDensity);

        return new TurnSignals(sentiment, arousal, directive, confusion, pace, memoryDensity);
    }
}
