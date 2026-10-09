using JLEngine.Runtime.Tools;

namespace JLEngine.Bridges.A2a;

/// <summary>Port of a2a_server.jl's `_a2a_agent_card` (the modern-format
/// discovery card). The legacy-format card (`_a2a_legacy_agent_card`) and
/// ACP pricing/payment blocks are out of scope for this pass — see the
/// class docs on A2aServer for the full scope decision.</summary>
public static class A2aAgentCard
{
    public const string AgentName = "JL Engine";
    public const string Version = "1.1.0";
    public const string ProtocolVersion = "1.0";
    public static readonly string[] DefaultInputModes = ["text/plain", "application/json"];
    public static readonly string[] DefaultOutputModes = ["application/json", "text/plain"];

    public static Dictionary<string, object?> Build(string publicUrl, IReadOnlyDictionary<string, ToolSchemaEntry> tools)
    {
        var baseUrl = publicUrl.TrimEnd('/');
        var skills = tools.Values.Select(t => new Dictionary<string, object?>
        {
            ["id"] = t.Name,
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["tags"] = new List<object?> { t.IsDynamic ? "dynamic" : "builtin" },
        }).ToList();

        var card = new Dictionary<string, object?>
        {
            ["name"] = AgentName,
            ["description"] = "C#/.NET-native AI agent engine with behavioral middleware stack " +
                "(DriftPressure, RhythmEngine, EmotionalAperture), persistent SQLite memory, " +
                "self-extending tool forge, and browser/network tool integrations.",
            ["version"] = Version,
            ["provider"] = new Dictionary<string, object?> { ["organization"] = "JL Engine", ["url"] = baseUrl },
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["streaming"] = false, // SSE streaming is out of scope for this pass
                ["pushNotifications"] = false, // out of scope for this pass
                ["extendedAgentCard"] = A2aBilling.AuthRequired,
            },
            ["supportedInterfaces"] = new List<object?>
            {
                new Dictionary<string, object?> { ["url"] = baseUrl, ["protocolBinding"] = "JSONRPC", ["protocolVersion"] = ProtocolVersion },
            },
            ["defaultInputModes"] = DefaultInputModes.ToList(),
            ["defaultOutputModes"] = DefaultOutputModes.ToList(),
            ["skills"] = skills,
        };

        if (A2aBilling.AuthRequired)
        {
            card["securitySchemes"] = new Dictionary<string, object?>
            {
                ["bearerAuth"] = new Dictionary<string, object?> { ["type"] = "http", ["scheme"] = "bearer" },
            };
            card["securityRequirements"] = new List<object?> { new Dictionary<string, object?> { ["bearerAuth"] = new List<object?>() } };
        }

        return card;
    }
}
