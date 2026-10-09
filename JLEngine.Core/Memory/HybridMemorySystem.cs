namespace JLEngine.Core.Memory;

/// <summary>
/// Port of Memory.jl. Pure in-process state (no SQLite here — durability is
/// bolted on by the outer runtime layer in Julia too; this class is
/// ephemeral, process-lifetime-only memory, matching the original).
///
/// Note: Julia's `update_after_turn!` truncates text via a byte-index slice
/// (`str[1:min(end,400)]`) that throws `StringIndexError` on multi-byte
/// UTF-8 boundaries — a defect, not a behavior. This port truncates to the
/// same ~400-character intent using SafeTruncate, which never throws.
/// </summary>
public sealed class HybridMemorySystem
{
    private const int MaxRecentEvents = 32;
    private const int MaxBreadcrumbs = 200;
    private const int MaxInteractionsPerAgent = 20;

    public Dictionary<string, object?> Shared { get; } = new()
    {
        ["last_active_agent"] = null,
        ["recent_events"] = new List<object?>(),
        ["engine_flags"] = new Dictionary<string, object?>(),
        ["user_profile"] = new Dictionary<string, object?>(),
        ["breadcrumbs"] = new List<object?>(),
    };

    public Dictionary<string, Dictionary<string, object?>> AgentStore { get; } = [];

    private static string NormalizeIntent(string? value)
    {
        if (value is null) return "general";
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "general" : normalized;
    }

    private Dictionary<string, object?> EnsureAgent(string agentId)
    {
        if (AgentStore.TryGetValue(agentId, out var existing)) return existing;
        var fresh = new Dictionary<string, object?>
        {
            ["recent_interactions"] = new List<object?>(),
            ["mood"] = "neutral",
            ["notes"] = new Dictionary<string, object?>(),
            ["dynamic_state"] = new Dictionary<string, object?>(),
        };
        AgentStore[agentId] = fresh;
        return fresh;
    }

    public Dictionary<string, object?> GetContext(string agentId)
    {
        var agentMemory = EnsureAgent(agentId);
        return new Dictionary<string, object?>
        {
            ["shared_memory"] = Shared,
            ["agent_memory"] = agentMemory,
        };
    }

    public void NoteEvent(string agentId, string eventType, Dictionary<string, object?>? payload = null)
    {
        EnsureAgent(agentId);
        var events = (List<object?>)Shared["recent_events"]!;
        events.Add(new Dictionary<string, object?>
        {
            ["agent"] = agentId,
            ["event_type"] = eventType,
            ["payload"] = payload ?? new Dictionary<string, object?>(),
        });
        TrimFront(events, MaxRecentEvents);
    }

    public void AddBreadcrumb(string agentId, string? intent, string kind, Dictionary<string, object?>? payload = null)
    {
        EnsureAgent(agentId);
        var breadcrumbs = (List<object?>)Shared["breadcrumbs"]!;
        breadcrumbs.Add(new Dictionary<string, object?>
        {
            ["agent"] = agentId,
            ["intent"] = NormalizeIntent(intent),
            ["kind"] = kind,
            ["payload"] = payload ?? new Dictionary<string, object?>(),
        });
        TrimFront(breadcrumbs, MaxBreadcrumbs);
    }

    public List<object?> GetBreadcrumbs(string? intent = null, int limit = 40)
    {
        var items = (List<object?>)Shared["breadcrumbs"]!;
        var filtered = intent is null
            ? items
            : items.Where(item => item is Dictionary<string, object?> d && (d.GetValueOrDefault("intent") as string) == NormalizeIntent(intent)).ToList();

        if (limit <= 0) return filtered;
        var startIndex = Math.Max(0, filtered.Count - limit);
        return filtered.Skip(startIndex).ToList();
    }

    public Dictionary<string, object?> GetIntentContext(string? intent = null, int limit = 24) => new()
    {
        ["intent"] = NormalizeIntent(intent),
        ["breadcrumbs"] = GetBreadcrumbs(intent, limit),
    };

    public void UpdateAfterTurn(string agentId, string userMessage, string output, Dictionary<string, object?> engineState)
    {
        var agentMemory = EnsureAgent(agentId);
        var interactions = (List<object?>)agentMemory["recent_interactions"]!;
        interactions.Add(new Dictionary<string, object?>
        {
            ["user_message"] = SafeTruncate(userMessage, 400),
            ["output"] = SafeTruncate(output, 400),
            ["engine_snapshot"] = new Dictionary<string, object?>
            {
                ["gait"] = engineState.GetValueOrDefault("gait"),
                ["rhythm"] = engineState.GetValueOrDefault("rhythm"),
                ["aperture"] = engineState.GetValueOrDefault("aperture_mode"),
                ["dynamic"] = engineState.GetValueOrDefault("dynamic"),
            },
        });
        TrimFront(interactions, MaxInteractionsPerAgent);

        Shared["last_active_agent"] = agentId;
        if (engineState.GetValueOrDefault("flags") is Dictionary<string, object?> flags)
        {
            var engineFlags = (Dictionary<string, object?>)Shared["engine_flags"]!;
            foreach (var (k, v) in flags) engineFlags[k] = v;
        }

        if (engineState.GetValueOrDefault("dynamic") is Dictionary<string, object?> dynamicState)
        {
            agentMemory["dynamic_state"] = new Dictionary<string, object?>(dynamicState);
        }
    }

    private static void TrimFront(List<object?> list, int max)
    {
        if (list.Count > max)
        {
            list.RemoveRange(0, list.Count - max);
        }
    }

    /// <summary>Truncates to at most maxLength UTF-16 code units, backing off by
    /// one if that would split a surrogate pair — never throws, unlike the
    /// Julia source's byte-index slice.</summary>
    private static string SafeTruncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]) && cut < text.Length && char.IsLowSurrogate(text[cut]))
        {
            cut--;
        }
        return text[..cut];
    }
}
