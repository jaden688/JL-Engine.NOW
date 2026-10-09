using System.Text.Json;
using JLEngine.Bridges;
using Xunit;

namespace JLEngine.Bridges.Tests;

public class CardCruncherTests
{
    private static string WriteJsonCard(object card)
    {
        var path = Path.Combine(Path.GetTempPath(), $"card-{Guid.NewGuid()}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(card));
        return path;
    }

    [Fact]
    public void ParseCard_V1Json_MarksVersionV1()
    {
        var path = WriteJsonCard(new { name = "Ada", description = "A witty analyst.", personality = "stoic, analytical, cool" });
        var card = CardCruncher.ParseCard(path);
        Assert.Equal("v1", card["_card_version"]);
        Assert.Equal("Ada", card["name"]);
    }

    [Fact]
    public void ParseCard_V2Spec_UnwrapsDataAndMarksVersionV2()
    {
        var path = WriteJsonCard(new { spec = "chara_card_v2", data = new { name = "Byte", description = "desc" } });
        var card = CardCruncher.ParseCard(path);
        Assert.Equal("v2", card["_card_version"]);
        Assert.Equal("Byte", card["name"]);
    }

    [Fact]
    public void InferArchetype_MatchesKeywordsInPersonality()
    {
        // Single-word archetypes (tsundere/kuudere/yandere/dandere) require that
        // literal word to appear in the text — the 4th tuple field is TAGS to
        // attach, not alternate matching keywords. Use a multi-keyword pattern
        // entry ("dark|brooding|cynical|edgy") to exercise real synonym matching.
        var (id, label, tags) = CardCruncher.InferArchetype("", "dark, brooding, cynical and intense", "Nyx");
        Assert.Equal("dark-brooding", id);
        Assert.Equal("dark", label);
        Assert.Contains("intense", tags);
    }

    [Fact]
    public void InferArchetype_NoKeywordMatch_FallsBackToGenericCharacterAgent()
    {
        var (id, label, _) = CardCruncher.InferArchetype("just a normal person", "ordinary", "Sam");
        Assert.Equal("character-agent", id);
        Assert.Equal("character", label);
    }

    [Fact]
    public void ParseDirectives_CapsAtEightAndStripsBulletMarkers()
    {
        var personality = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"- Directive number {i} is fairly detailed"));
        var directives = CardCruncher.ParseDirectives(personality);
        Assert.Equal(8, directives.Count);
        Assert.DoesNotContain(directives, d => d.StartsWith('-'));
    }

    [Fact]
    public void BuildBootPrompt_PrefersExistingSystemPromptAndSubstitutesPlaceholders()
    {
        var prompt = CardCruncher.BuildBootPrompt("Nova", "desc", "pers", "scenario", "hi", "You are {{char}}, talking to {{user}}.");
        Assert.Contains("You are Nova, talking to User.", prompt);
        Assert.Contains("[JLEngine: Character agent loaded from SillyTavern card", prompt);
    }

    [Fact]
    public void CardToAgent_ProducesValidJlEngineAgentCardShape()
    {
        var path = WriteJsonCard(new { name = "Nova", description = "A calm mentor.", personality = "wise, calm, guiding, ancient", scenario = "", first_mes = "Hello there.", system_prompt = "" });
        var card = CardCruncher.ParseCard(path);
        var agent = CardCruncher.CardToAgent(card, path);

        Assert.True(agent.ContainsKey("identity"));
        Assert.True(agent.ContainsKey("engine_alignment"));
        Assert.True(agent.ContainsKey("emotion_wheel"));
        var llmProfiles = Assert.IsType<Dictionary<string, object?>>(agent["llm_profiles"]);
        var genericLlm = Assert.IsType<Dictionary<string, object?>>(llmProfiles["generic_llm"]);
        Assert.False(string.IsNullOrWhiteSpace(genericLlm["boot_prompt"] as string));

        var identity = Assert.IsType<Dictionary<string, object?>>(agent["identity"]);
        Assert.Equal("Nova", identity["name"]);
        // "role" holds the archetype LABEL ("sage"); "archetype" holds the
        // longer id ("mentor-sage") — matches Julia's role=>archetype_label mapping.
        Assert.Equal("sage", identity["role"]);
        Assert.Equal("mentor-sage", identity["archetype"]);
    }
}
