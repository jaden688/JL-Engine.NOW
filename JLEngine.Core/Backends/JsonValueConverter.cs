using System.Text.Json.Nodes;

namespace JLEngine.Core.Backends;

/// <summary>Converts the loosely-typed Dictionary/List/primitive object graphs used
/// throughout this port (mirroring Julia's Dict{String,Any}) to/from JsonNode for
/// HTTP request/response bodies.</summary>
public static class JsonValueConverter
{
    public static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        Dictionary<string, object?> dict => new JsonObject(dict.Select(kv =>
            KeyValuePair.Create(kv.Key, ToJsonNode(kv.Value)))),
        List<object?> list => new JsonArray(list.Select(ToJsonNode).ToArray()),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        double d => JsonValue.Create(d),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        _ => JsonValue.Create(value.ToString()),
    };
}
