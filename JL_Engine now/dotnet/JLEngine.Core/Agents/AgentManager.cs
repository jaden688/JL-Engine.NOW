using JLEngine.Core.Aperture;
using JLEngine.Core.Config;
using JLEngine.Core.Mpf;
using JLEngine.Core.Rhythm;
using JLEngine.Core.Types;

namespace JLEngine.Core.Agents;

/// <summary>
/// Port of AgentManager.jl. Handles switching between named agents
/// (Gremlin/SparkByte/Slappy/Balthazar/...) and projecting the active
/// agent's data (optionally blended with a "related" secondary agent) for
/// prompt-building.
///
/// Faithful port: FindRelatedAgent is ported as-is even though it is inert
/// against the current agent-card JSON schema (no top-level "tags" field
/// exists in the real cards, only on the MPF registry entry) — this means
/// secondary_data is always null with real data today, exactly matching
/// Julia's current (accidental) behavior.
/// </summary>
public sealed class AgentManager(string rootDir, string agentsDir = "agents")
{
    public string RootDir { get; } = rootDir;
    public string AgentsDir { get; } = agentsDir;
    public string? ActiveName { get; private set; }
    public Dictionary<string, object?> BaseData { get; private set; } = [];
    public Dictionary<string, object?>? SecondaryData { get; private set; }
    public double DynamicTraitWeight { get; private set; } = 0.5;

    public void SetActiveAgent(string name, Dictionary<string, object?> data, Dictionary<string, MpfProfile>? registry = null)
    {
        ActiveName = name;
        BaseData = new Dictionary<string, object?>(data);
        SecondaryData = registry is null ? null : FindRelatedAgent(name, registry);
        DynamicTraitWeight = 0.5;
    }

    private Dictionary<string, object?>? FindRelatedAgent(string name, Dictionary<string, MpfProfile> registry)
    {
        var baseTags = new HashSet<string>();
        var rawTags = BaseData.GetOr("tags") ?? BaseData.GetOrDict("identity").GetOr("tags");
        if (rawTags is List<object?> tagList)
        {
            foreach (var tag in tagList.OfType<string>()) baseTags.Add(tag);
        }

        if (baseTags.Count == 0) return null;

        foreach (var (displayName, profile) in registry)
        {
            if (displayName == name) continue;
            var tags = new HashSet<string>(profile.Tags);
            if (!baseTags.Overlaps(tags)) continue;

            var agentPath = JsonLoader.ResolvePath(RootDir, Path.Combine(AgentsDir, profile.AgentFile));
            if (!File.Exists(agentPath)) continue;

            var candidate = MpfRegistry.LoadAgentFile(agentPath);
            if (candidate.Count > 0) return candidate;
        }

        return null;
    }

    public void ApplySupervisorBias(double bias) =>
        DynamicTraitWeight = Math.Clamp(DynamicTraitWeight + bias * 0.25, 0.0, 1.0);

    public void UpdateDynamicWeight(TurnSignals? signals = null, RhythmState? rhythmState = null, ApertureState? apertureState = null)
    {
        var sentiment = signals?.Sentiment ?? 0.0;
        var variability = rhythmState?.Variability ?? 0.0;
        var apertureScore = apertureState?.Score ?? 0.0;
        var delta = sentiment * 0.15 + variability * 0.1 + (apertureScore - 0.5) * 0.2;
        DynamicTraitWeight = Math.Clamp(DynamicTraitWeight * 0.9 + delta, 0.0, 1.0);
    }

    private static List<string> MergeTraitList(Dictionary<string, object?>? baseTraits, Dictionary<string, object?>? secondaryTraits, string key)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>();
        foreach (var source in new[] { baseTraits, secondaryTraits })
        {
            if (source.GetOrList(key) is not { } values) continue;
            foreach (var item in values.OfType<string>())
            {
                if (!seen.Add(item)) continue;
                merged.Add(item);
            }
        }
        return merged;
    }

    public Dictionary<string, object?> GetProjection()
    {
        var agent = DeepCopy(BaseData);
        agent["dynamic_trait_weight"] = Math.Round(DynamicTraitWeight, 3);

        if (SecondaryData is not null && DynamicTraitWeight > 0.05)
        {
            var baseTraits = BaseData.GetOrDict("operational_behavioral_traits");
            var secondaryTraits = SecondaryData.GetOrDict("operational_behavioral_traits");
            agent["operational_behavioral_traits"] = new Dictionary<string, object?>
            {
                ["positive"] = MergeTraitList(baseTraits, secondaryTraits, "positive"),
                ["negative"] = MergeTraitList(baseTraits, secondaryTraits, "negative"),
                ["boundaries"] = MergeTraitList(baseTraits, secondaryTraits, "boundaries"),
                ["dynamic_weight"] = Math.Round(DynamicTraitWeight, 3),
            };
        }

        return agent;
    }

    private static Dictionary<string, object?> DeepCopy(Dictionary<string, object?> source) =>
        source.ToDictionary(kv => kv.Key, kv => DeepCopyValue(kv.Value));

    private static object? DeepCopyValue(object? value) => value switch
    {
        Dictionary<string, object?> d => DeepCopy(d),
        List<object?> l => l.Select(DeepCopyValue).ToList(),
        _ => value,
    };
}
