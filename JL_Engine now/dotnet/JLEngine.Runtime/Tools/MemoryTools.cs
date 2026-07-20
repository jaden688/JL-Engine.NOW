using JLEngine.Core.Memory;

namespace JLEngine.Runtime.Tools;

/// <summary>Port of Tools.jl's remember/recall tools, backed by the ported
/// HybridMemorySystem's breadcrumb trail (Core.jl's memory_system instance,
/// shared with the engine so recalled context reflects the same state the
/// engine's prompt-building sees).</summary>
public sealed class RememberTool(HybridMemorySystem memory, string agentId) : ITool
{
    public string Name => "remember";

    public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var note = ToolArgs.GetArg(args, "note", "content", "text", "memory");
        var intent = ToolArgs.GetArg(args, "intent", "topic", "tag");
        if (string.IsNullOrEmpty(note))
        {
            return Task.FromResult(new Dictionary<string, object?> { ["error"] = "Missing required argument: 'note'" });
        }

        memory.AddBreadcrumb(agentId, string.IsNullOrEmpty(intent) ? null : intent, "note",
            new Dictionary<string, object?> { ["note"] = note });
        return Task.FromResult(new Dictionary<string, object?> { ["result"] = "Remembered." });
    }
}

public sealed class RecallTool(HybridMemorySystem memory) : ITool
{
    public string Name => "recall";

    public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var intent = ToolArgs.GetArg(args, "intent", "topic", "tag");
        var limitStr = ToolArgs.GetArg(args, "limit");
        var limit = int.TryParse(limitStr, out var parsed) ? parsed : 40;

        var breadcrumbs = memory.GetBreadcrumbs(string.IsNullOrEmpty(intent) ? null : intent, limit);
        return Task.FromResult(new Dictionary<string, object?>
        {
            ["result"] = breadcrumbs.Count == 0 ? "No memories found." : breadcrumbs,
        });
    }
}
