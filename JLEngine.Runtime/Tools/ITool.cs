namespace JLEngine.Runtime.Tools;

/// <summary>
/// Port of Tools.jl's tool_* function convention. Each tool takes the raw
/// args dict from the LLM's function call and returns a result dict —
/// matching Julia's "always return a Dict, catch exceptions inline into an
/// {error: ...} entry" convention rather than throwing.
/// </summary>
public interface ITool
{
    string Name { get; }
    Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args);
}
