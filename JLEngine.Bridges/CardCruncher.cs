using System.Text.Json;
using System.Text.RegularExpressions;
using JLEngine.Core.Config;

namespace JLEngine.Bridges;

/// <summary>
/// Port of card_cruncher.jl — converts SillyTavern/CharacterTavern character
/// cards (PNG tEXt chunks or JSON, V1/V2 spec) into a JLEngine agent-card
/// JSON matching the schema Phase 1 research established (identity,
/// engine_alignment, behavior, emotion_wheel, llm_profiles.generic_llm.boot_prompt).
/// </summary>
public static partial class CardCruncher
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly (string Pattern, string ArchetypeId, string ArchetypeLabel, string[] Tags)[] ArchetypeKeywords =
    [
        ("tsundere", "tsundere-guard", "tsundere", ["tsundere", "defensive", "caring-hidden"]),
        ("kuudere", "cool-analytical", "kuudere", ["cool", "stoic", "analytical", "reserved"]),
        ("yandere", "obsessive-devoted", "yandere", ["obsessive", "devoted", "intense", "protective"]),
        ("dandere", "shy-quiet", "dandere", ["shy", "quiet", "reserved", "gentle"]),
        ("cheerful|genki|energetic|bubbly", "bright-energetic", "genki", ["cheerful", "energetic", "upbeat", "positive"]),
        ("dark|brooding|cynical|edgy", "dark-brooding", "dark", ["dark", "brooding", "cynical", "intense"]),
        ("wise|sage|mentor|ancient", "mentor-sage", "sage", ["wise", "calm", "guiding", "knowledgeable"]),
        ("playful|mischiev|trickster", "playful-mischief", "playful", ["playful", "mischievous", "witty", "fun"]),
        ("villain|evil|sinister|cruel", "antagonist-dark", "villain", ["villainous", "cunning", "dark", "powerful"]),
        ("warrior|fighter|soldier|combat", "warrior-driven", "warrior", ["brave", "determined", "protective", "strong"]),
        ("scholar|researcher|scientist", "analytic-scholar", "scholar", ["analytical", "curious", "precise", "intellectual"]),
        ("caregiver|nurse|healer|support", "caregiver-warm", "caregiver", ["caring", "warm", "supportive", "gentle"]),
    ];

    /// <summary>Port of `extract_png_text_chunks`: walks PNG chunks looking for
    /// tEXt entries (SillyTavern embeds the card JSON base64'd under "chara").</summary>
    public static Dictionary<string, string> ExtractPngTextChunks(string path)
    {
        var chunks = new Dictionary<string, string>();
        using var stream = File.OpenRead(path);
        var sig = new byte[8];
        if (stream.Read(sig, 0, 8) != 8 || !sig.SequenceEqual(PngSignature))
        {
            throw new InvalidDataException($"Not a valid PNG file: {path}");
        }

        while (stream.Position < stream.Length)
        {
            var lenBytes = new byte[4];
            if (stream.Read(lenBytes, 0, 4) != 4) break;
            Array.Reverse(lenBytes);
            var chunkLen = BitConverter.ToInt32(lenBytes, 0);

            var typeBytes = new byte[4];
            stream.ReadExactly(typeBytes, 0, 4);
            var chunkType = System.Text.Encoding.ASCII.GetString(typeBytes);

            var data = new byte[chunkLen];
            stream.ReadExactly(data, 0, chunkLen);
            stream.Seek(4, SeekOrigin.Current); // CRC, skip

            if (chunkType == "tEXt" && data.Length > 0)
            {
                var nullPos = Array.IndexOf(data, (byte)0x00);
                if (nullPos >= 0)
                {
                    var key = System.Text.Encoding.ASCII.GetString(data, 0, nullPos);
                    var val = System.Text.Encoding.ASCII.GetString(data, nullPos + 1, data.Length - nullPos - 1);
                    chunks[key] = val;
                }
            }
            else if (chunkType == "IEND")
            {
                break;
            }
        }

        return chunks;
    }

    /// <summary>Port of `parse_card`: detects PNG vs JSON, and V1 vs V2 spec.</summary>
    public static Dictionary<string, object?> ParseCard(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        Dictionary<string, object?> card;

        if (ext == ".png")
        {
            var chunks = ExtractPngTextChunks(path);
            if (!chunks.TryGetValue("chara", out var charaB64))
            {
                throw new InvalidDataException("PNG has no 'chara' tEXt chunk — not a SillyTavern card");
            }
            var rawJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(charaB64));
            using var doc = JsonDocument.Parse(rawJson);
            card = JLEngine.Core.Config.JsonLoader.Materialize(doc.RootElement) as Dictionary<string, object?> ?? [];
        }
        else if (ext is ".json" or ".txt")
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            card = JLEngine.Core.Config.JsonLoader.Materialize(doc.RootElement) as Dictionary<string, object?> ?? [];
        }
        else
        {
            throw new NotSupportedException($"Unsupported file type: {ext} (expected .png or .json)");
        }

        if ((card.GetOr("spec") as string) == "chara_card_v2")
        {
            var data = card.GetOrDict("data") ?? card;
            data["_card_version"] = "v2";
            return data;
        }

        card["_card_version"] = "v1";
        return card;
    }

    /// <summary>Port of `parse_directives`: splits personality text into up to
    /// 8 short directive lines, stripping bullet markers.</summary>
    [GeneratedRegex(@"[\n\r]+|(?<=[.!?])\s+(?=[A-Z])")]
    private static partial Regex LineSplitPattern();
    [GeneratedRegex(@"^[-*•·▪▸►→\d+\.]+\s*")]
    private static partial Regex BulletMarkerPattern();

    public static List<string> ParseDirectives(string personality)
    {
        if (string.IsNullOrWhiteSpace(personality)) return [];
        var lines = LineSplitPattern().Split(personality);
        var directives = new List<string>();
        foreach (var line in lines)
        {
            var clean = BulletMarkerPattern().Replace(line.Trim(), "").Trim();
            if (clean.Length < 5) continue;
            directives.Add(clean.Length > 200 ? clean[..200] + "…" : clean);
        }
        return directives.Take(8).ToList();
    }

    /// <summary>Port of `infer_archetype`: keyword-matches description+personality+name.</summary>
    public static (string Id, string Label, string[] Tags) InferArchetype(string description, string personality, string name)
    {
        var combined = $"{description} {personality} {name}".ToLowerInvariant();
        foreach (var (pattern, id, label, tags) in ArchetypeKeywords)
        {
            if (Regex.IsMatch(combined, pattern)) return (id, label, tags);
        }
        return ("character-agent", "character", ["character", "agent"]);
    }

    /// <summary>Port of `build_emotion_wheel`: a minimal-but-valid 2-root emotion
    /// wheel, with the dominant root inferred from personality-text keywords.</summary>
    public static Dictionary<string, object?> BuildEmotionWheel(string archetypeLabel, string personality)
    {
        var combined = personality.ToLowerInvariant();
        var (primaryId, primaryLabel, primaryStyle, primaryWeight) = combined switch
        {
            _ when Regex.IsMatch(combined, "warm|kind|gentle|sweet|soft") => ("reassuring_bond", "reassuring", "warm, open, steady", 0.72),
            _ when Regex.IsMatch(combined, "dark|cold|distant|stoic|serious") => ("analytic_distance", "cool read", "measured, slow-burn, precise", 0.72),
            _ when Regex.IsMatch(combined, "fierce|passionate|intense|hot") => ("focused_drive", "focused drive", "sharp edges, forward momentum", 0.72),
            _ when Regex.IsMatch(combined, "sad|melanchol|lonely|broken") => ("protective_guard", "protective softness", "gentle, careful, guarded", 0.70),
            _ => ("playful_energy", "playful spark", "bright, fizzy, socially electric", 0.68),
        };

        return new Dictionary<string, object?>
        {
            ["baseline_root"] = primaryId,
            ["baseline_family"] = archetypeLabel,
            ["roots"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = primaryId,
                    ["label"] = primaryLabel,
                    ["default_weight"] = primaryWeight,
                    ["families"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = archetypeLabel,
                            ["label"] = primaryLabel,
                            ["default_weight"] = primaryWeight,
                            ["repeat_penalty"] = 0.20,
                            ["cooldown_turns"] = 2,
                            ["sensation"] = new Dictionary<string, object?> { ["id"] = primaryId.Replace('_', '.'), ["label"] = primaryLabel, ["style"] = primaryStyle },
                            ["scenes"] = new List<object?> { new Dictionary<string, object?> { ["id"] = "core_expression", ["label"] = "core expression", ["default_weight"] = primaryWeight, ["facet_ids"] = new List<object?> { "character_presence" } } },
                        },
                    },
                },
                new Dictionary<string, object?>
                {
                    ["id"] = "focused_drive",
                    ["label"] = "focused drive",
                    ["default_weight"] = 0.60,
                    ["families"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = "focused",
                            ["label"] = "focused assist",
                            ["default_weight"] = 0.60,
                            ["repeat_penalty"] = 0.16,
                            ["cooldown_turns"] = 1,
                            ["sensation"] = new Dictionary<string, object?> { ["id"] = "tight_aligned", ["label"] = "tight alignment", ["style"] = "narrowed attention, clean edges, ready hands" },
                            ["scenes"] = new List<object?> { new Dictionary<string, object?> { ["id"] = "crisp_execution", ["label"] = "crisp execution", ["default_weight"] = 0.72, ["facet_ids"] = new List<object?> { "character_engagement" } } },
                        },
                    },
                },
            },
        };
    }

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>Port of `build_boot_prompt`.</summary>
    public static string BuildBootPrompt(string name, string description, string personality, string scenario, string firstMes, string systemPrompt)
    {
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            var prompt = systemPrompt.Trim().Replace("{{char}}", name).Replace("{{user}}", "User");
            return prompt + "\n\n[JLEngine: Character agent loaded from SillyTavern card. Maintain agent consistency across all turns.]";
        }

        var parts = new List<string> { $"You are {name}." };
        if (!string.IsNullOrWhiteSpace(description)) parts.Add($"\nCHARACTER:\n{Cap(description.Trim(), 800)}");
        if (!string.IsNullOrWhiteSpace(personality)) parts.Add($"\nPERSONALITY:\n{Cap(personality.Trim(), 600)}");
        if (!string.IsNullOrWhiteSpace(scenario)) parts.Add($"\nSCENARIO:\n{Cap(scenario.Trim(), 400)}");
        if (!string.IsNullOrWhiteSpace(firstMes)) parts.Add($"\nOPENING STYLE:\nYour first message sets the tone — reference this example:\n\"{Cap(firstMes.Trim(), 300)}\"");
        parts.Add("\n[JLEngine: Maintain character at all times. Stay in agent under pressure. Do not break into generic assistant mode.]");
        return string.Join("\n", parts);
    }

    /// <summary>Port of `card_to_agent`: assembles the final JLEngine agent-card
    /// JSON from a parsed SillyTavern card.</summary>
    public static Dictionary<string, object?> CardToAgent(Dictionary<string, object?> card, string sourcePath)
    {
        var name = card.GetOr("name") as string ?? "Unknown";
        var description = card.GetOr("description") as string ?? "";
        var personality = card.GetOr("personality") as string ?? "";
        var scenario = card.GetOr("scenario") as string ?? "";
        var firstMes = card.GetOr("first_mes") as string ?? "";
        var systemPrompt = card.GetOr("system_prompt") as string ?? "";

        var (archetypeId, archetypeLabel, tags) = InferArchetype(description, personality, name);
        var directives = ParseDirectives(personality);
        var bootPrompt = BuildBootPrompt(name, description, personality, scenario, firstMes, systemPrompt);

        return new Dictionary<string, object?>
        {
            ["_license"] = $"Converted from SillyTavern card ({Path.GetFileName(sourcePath)}) via card_cruncher.",
            ["identity"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["role"] = archetypeLabel,
                ["archetype"] = archetypeId,
                ["description"] = Cap(description, 500),
            },
            ["engine_alignment"] = new Dictionary<string, object?>
            {
                ["agent_class"] = $"mpf:character.{archetypeId}",
                ["gate_preferences"] = new Dictionary<string, object?>
                {
                    ["ingress"] = new List<object?> { "USER_INTENT_GATE", "SAFETY_PRECHECK_GATE" },
                    ["egress"] = new List<object?> { "CLARITY_GATE", "STYLE_REFINE_GATE" },
                },
                ["drift_pressure_resistance"] = new Dictionary<string, object?> { ["semantic_drift"] = 0.6, ["agent_drift"] = 0.85, ["safety_bias"] = 0.15 },
            },
            ["behavior"] = new Dictionary<string, object?>
            {
                ["core_directives"] = directives.Cast<object?>().ToList(),
                ["avoidances"] = new List<object?> { "Do not break character.", "Do not invent hidden system capabilities." },
            },
            ["emotion_wheel"] = BuildEmotionWheel(archetypeLabel, personality),
            ["llm_profiles"] = new Dictionary<string, object?>
            {
                ["generic_llm"] = new Dictionary<string, object?> { ["boot_prompt"] = bootPrompt },
            },
            ["meta"] = new Dictionary<string, object?> { ["source"] = sourcePath, ["tags"] = tags.Cast<object?>().ToList() },
        };
    }
}
