namespace JLEngine.Core.Backends;

/// <summary>
/// Port of Backends.jl's BACKEND_REGISTRY / ACTIVE_BACKENDS / get_backend /
/// get_brain_backend / get_tool_backend / configure_backends! family.
///
/// Note: Julia's ACTIVE_BACKENDS is module-level GLOBAL mutable state,
/// shared by every JLEngineCore in the process. That's a forced-by-language
/// change here, not a behavior change (the Julia app is single-instance
/// today): this registry is instance-scoped, owned by one JLEngineCore.
/// </summary>
public sealed class BackendRegistry
{
    public static readonly IReadOnlyDictionary<string, Dictionary<string, object?>> StaticEntries = new Dictionary<string, Dictionary<string, object?>>
    {
        ["noop-stub"] = new()
        {
            ["id"] = "noop-stub",
            ["label"] = "Stub (No backend)",
            ["provider"] = "noop",
        },
        ["openrouter"] = new()
        {
            ["id"] = "openrouter",
            ["label"] = "OpenRouter",
            ["provider"] = "openrouter",
            ["endpoint"] = OpenRouterBackend.DefaultEndpoint,
            ["model"] = OpenRouterBackend.DefaultModel,
            ["api_key"] = null,
            ["timeout"] = 120,
        },
    };

    private readonly Dictionary<string, Dictionary<string, object?>> _registry =
        StaticEntries.ToDictionary(kv => kv.Key, kv => new Dictionary<string, object?>(kv.Value));

    private readonly Dictionary<string, string> _activeBackends = new()
    {
        ["current"] = "openrouter",
        ["brain"] = "openrouter",
        ["tool"] = "openrouter",
    };

    public void SetBackendModel(string backendId, string modelName)
    {
        if (!_registry.TryGetValue(backendId, out var entry)) return;
        entry["modelName"] = modelName;
        entry["model_name"] = modelName;
        entry["model"] = modelName;
    }

    public void ConfigureBackends(string? brainId = null, string? toolId = null)
    {
        if (brainId is not null) SetBrainBackendId(brainId);
        if (toolId is not null) SetToolBackendId(toolId);
    }

    public void SetBrainBackendId(string backendId)
    {
        if (!_registry.ContainsKey(backendId)) return;
        _activeBackends["brain"] = backendId;
        _activeBackends["current"] = backendId;
    }

    public void SetToolBackendId(string backendId)
    {
        if (!_registry.ContainsKey(backendId)) return;
        _activeBackends["tool"] = backendId;
    }

    public IBackend GetBackend(string? backendId = null, Dictionary<string, object?>? overrides = null)
    {
        var targetId = backendId ?? _activeBackends["current"];
        var baseConfig = _registry.GetValueOrDefault(targetId, _registry["noop-stub"]);
        var config = new Dictionary<string, object?>(baseConfig);
        if (overrides is not null)
        {
            foreach (var (k, v) in overrides) config[k] = v;
        }

        var provider = config.GetValueOrDefault("provider") as string ?? "noop";
        return provider == "openrouter" ? new OpenRouterBackend(config) : new NoopBackend(config);
    }

    public IBackend GetBrainBackend() => GetBackend(_activeBackends["brain"]);
    public IBackend GetToolBackend() => GetBackend(_activeBackends["tool"]);
}
