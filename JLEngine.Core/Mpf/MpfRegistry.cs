using JLEngine.Core.Config;
using JLEngine.Core.Types;

namespace JLEngine.Core.Mpf;

/// <summary>
/// Port of MPF.jl. Note this is much thinner than the name suggests: it only
/// builds the agent registry from Agents.mpf.json. The individual agent card
/// files (e.g. The_Gremlin_Full.json) are loaded as raw untyped dicts and
/// never coerced into a schema — all downstream consumption reads fields
/// out of the dict defensively via GetOr(...), matching Julia's duck typing.
/// </summary>
public static class MpfRegistry
{
    public static Dictionary<string, MpfProfile> LoadMpfRegistry(string registryPath)
    {
        var rawRegistry = JsonLoader.LoadJsonSafely(registryPath);
        var profiles = new Dictionary<string, MpfProfile>();

        foreach (var (displayName, entryObj) in rawRegistry)
        {
            if (entryObj is not Dictionary<string, object?> entry) continue;
            if (entry.GetOr("agent_file") is not string agentFile) continue;

            var tags = (entry.GetOrList("tags") ?? [])
                .OfType<string>()
                .ToList();

            profiles[displayName] = new MpfProfile
            {
                AgentFile = agentFile,
                DefaultMemoryMode = entry.GetOr("default_memory_mode") as string,
                DefaultBackendId = entry.GetOr("default_backend_id") as string,
                DriveType = entry.GetOr("drive_type") as string,
                Tags = tags,
            };
        }

        return profiles;
    }

    public static Dictionary<string, object?> LoadAgentFile(string path) => JsonLoader.LoadJsonSafely(path);

    /// <summary>
    /// Loads specialist persona config from a modular fat-agent package while
    /// keeping the same MPF/engine contract. This is the hook where the
    /// "little monsters" are bound to the engine rather than living as dead JSON.
    /// </summary>
    public static Dictionary<string, SpecialistAgentProfile> LoadSpecialistProfiles(string rootDir)
    {
        var profiles = new Dictionary<string, SpecialistAgentProfile>(StringComparer.OrdinalIgnoreCase);
        var packageDir = Path.Combine(rootDir, "modular_fat_agent_pack");
        if (!Directory.Exists(packageDir)) return profiles;

        var fatAgentDir = Path.Combine(packageDir, "fat_agents");
        if (!Directory.Exists(fatAgentDir)) return profiles;

        foreach (var jsonFile in Directory.EnumerateFiles(fatAgentDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            var raw = JsonLoader.LoadJsonSafely(jsonFile);
            if (raw.Count == 0) continue;

            var identity = raw.GetOrDict("identity") ?? [];
            var name = identity.GetOrString("name", Path.GetFileNameWithoutExtension(jsonFile));
            var engines = raw.GetOrDict("engine_alignment") ?? [];
            var gears = raw.GetOrDict("cognitive_gears") ?? [];
            var modes = raw.GetOrDict("cognitive_modes") ?? [];
            var stateModulation = engines.GetOrDict("state_modulation_profile") ?? [];
            var routing = engines.GetOrDict("tool_routing") ?? [];

            var tags = (identity.GetOrList("tags") ?? [])
                .OfType<string>()
                .ToList();
            var preferredGears = (gears.GetOrList("preferred_gears") ?? [])
                .OfType<string>()
                .ToList();
            var activeModes = (modes.GetOrList("active_modes") ?? [])
                .OfType<string>()
                .ToList();

            profiles[name] = new SpecialistAgentProfile
            {
                Name = name,
                Role = identity.GetOrString("role", "specialist"),
                Description = identity.GetOrString("description", ""),
                Archetype = identity.GetOrString("archetype", ""),
                AgentClass = engines.GetOrString("agent_class", "mpf:assistant.specialist"),
                DefaultRoute = routing.GetOrString("default_route", "INTERPRETER_CORE"),
                DriveType = engines.GetOrString("drive_type", ""),
                DefaultMemoryMode = raw.GetOrDict("memory")?.GetOrString("default_memory_mode", "balanced") ?? "balanced",
                ToolProfile = raw.GetOrString("tool_profile", ""),
                StateProfile = stateModulation.GetOrString("baseline_state", "steady"),
                GateProfile = string.Join(",", (engines.GetOrDict("gate_preferences")?.GetOrList("ingress") ?? []).OfType<string>()),
                BehaviorProfile = raw.GetOrString("behavior_profile", ""),
                TaskProfile = raw.GetOrString("task_profile", ""),
                ToneProfile = raw.GetOrString("tone_profile", ""),
                BaselineState = stateModulation.GetOrString("baseline_state", "steady"),
                Tags = tags,
                PreferredGears = preferredGears,
                ActiveModes = activeModes,
                RawConfig = raw,
            };
        }

        return profiles;
    }

    public static string GetLlmBootPrompt(Dictionary<string, object?> agentConfig, string target = "generic_llm")
    {
        if (agentConfig.GetOrDict("llm_profiles") is not { } profiles) return "";

        if (profiles.GetOrDict(target)?.GetOr("boot_prompt") is string prompt)
        {
            return prompt;
        }

        if (profiles.GetOrDict("generic_llm")?.GetOr("boot_prompt") is string generic)
        {
            return generic;
        }

        return "";
    }
}
