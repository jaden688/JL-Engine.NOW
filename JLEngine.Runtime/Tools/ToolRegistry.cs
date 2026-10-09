using System.Text.Json;

namespace JLEngine.Runtime.Tools;

/// <summary>Schema entry for one tool, in OpenAI function-calling format
/// (lowercase types) — the wire format every live provider actually uses,
/// per the plan's decision to skip porting Schema.jl's Gemini-shaped
/// representation (which gets converted away before any network call anyway).</summary>
public sealed record ToolSchemaEntry(string Name, string Description, Dictionary<string, object?> Parameters, bool IsDynamic = false);

/// <summary>
/// Port of Tools.jl's TOOL_MAP + dispatch() + DYNAMIC_SCHEMA. Unifies
/// built-in ITool instances and dynamically forged tools (registered later
/// by ForgeNewToolTool) under one dispatch path, mirroring dispatch()'s
/// catch-all exception handling and per-call usage logging hook.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = [];
    private readonly Dictionary<string, ToolSchemaEntry> _dynamicSchema = [];
    public string StateDir { get; }

    /// <summary>Tools switched off from the GUI's tool-catalog panel for this
    /// session — excluded from the model's tools array and rejected in
    /// DispatchAsync (defense in depth against a call already in flight).</summary>
    public HashSet<string> DisabledTools { get; } = [];

    public Action<string, Dictionary<string, object?>, Dictionary<string, object?>, long>? OnToolUsage { get; set; }

    public ToolRegistry(string stateDir)
    {
        StateDir = stateDir;
        Directory.CreateDirectory(StateDir);
    }

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    public void RegisterDynamic(string name, ITool tool, ToolSchemaEntry schema)
    {
        _tools[name] = tool;
        _dynamicSchema[name] = schema;
    }

    public bool Contains(string name) => _tools.ContainsKey(name);

    public IReadOnlyCollection<string> ToolNames() => _tools.Keys;

    public async Task<Dictionary<string, object?>> DispatchAsync(string name, Dictionary<string, object?> args, string agent = "SparkByte")
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return new Dictionary<string, object?> { ["error"] = $"Unknown tool: '{name}'" };
        }
        if (DisabledTools.Contains(name))
        {
            return new Dictionary<string, object?> { ["error"] = $"Tool '{name}' is disabled for this session." };
        }

        var started = DateTime.UtcNow;
        Dictionary<string, object?> result;
        try
        {
            result = await tool.DispatchAsync(args);
        }
        catch (Exception e)
        {
            result = new Dictionary<string, object?> { ["error"] = e.Message };
        }
        var elapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds;

        OnToolUsage?.Invoke(name, args, result, elapsedMs);
        return result;
    }

    /// <summary>Builds the OpenAI-format `tools` array for a chat-completions
    /// request body, covering both built-in tools with statically-known
    /// schemas and forged dynamic tools.</summary>
    public List<Dictionary<string, object?>> BuildOpenAiToolsArray(IReadOnlyDictionary<string, ToolSchemaEntry> builtinSchemas)
    {
        var entries = builtinSchemas.Values.Concat(_dynamicSchema.Values).Where(e => !DisabledTools.Contains(e.Name));
        return entries.Select(e => new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = e.Name,
                ["description"] = e.Description,
                ["parameters"] = e.Parameters,
            },
        }).ToList();
    }

    public IReadOnlyDictionary<string, ToolSchemaEntry> DynamicSchema => _dynamicSchema;

    /// <summary>Full catalog for the GUI's tool panel: every built-in + stub +
    /// forged tool, with whether it's disabled for this session.</summary>
    public List<Dictionary<string, object?>> Catalog(IReadOnlyDictionary<string, ToolSchemaEntry> builtinSchemas) =>
        builtinSchemas.Values.Concat(_dynamicSchema.Values)
            .Select(e => new Dictionary<string, object?>
            {
                ["name"] = e.Name,
                ["description"] = e.Description,
                ["dynamic"] = e.IsDynamic,
                ["enabled"] = !DisabledTools.Contains(e.Name),
            })
            .ToList();

    /// <summary>Port of `_load_dynamic_tools!`: re-registers forged tools from
    /// the on-disk registry at boot. The actual code re-compilation happens
    /// in ForgeNewToolTool.LoadPersistedAsync, which this delegates to.</summary>
    public async Task LoadPersistedToolsAsync(Func<string, string, ToolSchemaEntry, Task> reforge)
    {
        var registryPath = Path.Combine(StateDir, "dynamic_tools_registry.json");
        if (!File.Exists(registryPath)) return;

        var codePath = Path.Combine(StateDir, "dynamic_tools_source.json");
        if (!File.Exists(codePath)) return;

        var registryJson = await File.ReadAllTextAsync(registryPath);
        var sourceJson = await File.ReadAllTextAsync(codePath);

        using var registryDoc = JsonDocument.Parse(registryJson);
        using var sourceDoc = JsonDocument.Parse(sourceJson);
        var sources = sourceDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

        foreach (var entry in registryDoc.RootElement.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            if (string.IsNullOrEmpty(name) || !sources.TryGetValue(name, out var code)) continue;

            var description = entry.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var parameters = entry.TryGetProperty("parameters", out var p)
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(p.GetRawText()) ?? []
                : [];

            await reforge(name, code, new ToolSchemaEntry(name, description, parameters, true));
        }
    }
}
