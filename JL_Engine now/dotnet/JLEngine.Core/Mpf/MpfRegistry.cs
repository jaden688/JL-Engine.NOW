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
