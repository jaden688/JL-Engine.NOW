using System.Text.Json;

namespace JLEngine.Core.Config;

/// <summary>
/// Port of Config.jl. Agent-card and engine JSON is deliberately kept as
/// loosely-typed Dictionary/List trees (mirroring Julia's Dict{String,Any}
/// duck-typing via `get(dict, key, default)`) rather than fixed DTOs,
/// since the real JSON files don't conform to one schema and the Julia
/// source never validates them either.
/// </summary>
public static class JsonLoader
{
    /// <summary>Port of `_materialize_json`: recursively flattens a JsonElement into
    /// plain Dictionary&lt;string, object?&gt; / List&lt;object?&gt; / primitive values.</summary>
    public static object? Materialize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject()
            .ToDictionary(p => p.Name, p => Materialize(p.Value)),
        JsonValueKind.Array => value.EnumerateArray()
            .Select(Materialize)
            .ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => null,
    };

    /// <summary>Port of `load_json_safely`: missing file, blank content, or any parse
    /// error silently yields an empty dict rather than throwing.</summary>
    public static Dictionary<string, object?> LoadJsonSafely(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var text = File.ReadAllText(path).Replace("﻿", "");
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return Materialize(doc.RootElement) as Dictionary<string, object?> ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Port of `resolve_path`.</summary>
    public static string ResolvePath(string rootDir, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(rootDir, path));

    /// <summary>Port of `load_engine_config`: pulls the "jl_engine" sub-object out of
    /// the master JSON blob (this is where core_rules etc. come from).</summary>
    public static Dictionary<string, object?> LoadEngineConfig(string path)
    {
        var blob = LoadJsonSafely(path);
        return blob.TryGetValue("jl_engine", out var jlBlob) && jlBlob is Dictionary<string, object?> dict
            ? dict
            : [];
    }
}

/// <summary>Julia-style defensive dict access: `get(dict, key, default)`.</summary>
public static class DictExtensions
{
    public static object? GetOr(this Dictionary<string, object?>? dict, string key, object? fallback = null) =>
        dict is not null && dict.TryGetValue(key, out var value) && value is not null ? value : fallback;

    public static string GetOrString(this Dictionary<string, object?>? dict, string key, string fallback = "") =>
        dict.GetOr(key) is string s ? s : fallback;

    public static double GetOrDouble(this Dictionary<string, object?>? dict, string key, double fallback = 0.0) =>
        dict.GetOr(key) switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };

    public static bool GetOrBool(this Dictionary<string, object?>? dict, string key, bool fallback = false) =>
        dict.GetOr(key) switch
        {
            bool b => b,
            _ => fallback,
        };

    public static Dictionary<string, object?>? GetOrDict(this Dictionary<string, object?>? dict, string key) =>
        dict.GetOr(key) as Dictionary<string, object?>;

    public static List<object?>? GetOrList(this Dictionary<string, object?>? dict, string key) =>
        dict.GetOr(key) as List<object?>;
}
