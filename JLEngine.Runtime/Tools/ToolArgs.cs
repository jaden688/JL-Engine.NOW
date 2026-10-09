namespace JLEngine.Runtime.Tools;

/// <summary>Port of Tools.jl's `_get_arg`/`_get_named_arg`: defensive argument
/// extraction that tries a primary key, then aliases, then (for `_get_arg`
/// only) falls back to "the single value present" or a nested "args" dict.</summary>
public static class ToolArgs
{
    public static string GetArg(Dictionary<string, object?> args, string primary, params string[] aliases)
    {
        if (args.TryGetValue(primary, out var v) && v is not null) return v.ToString() ?? "";
        foreach (var alias in aliases)
        {
            if (args.TryGetValue(alias, out var av) && av is not null) return av.ToString() ?? "";
        }
        if (args.Count == 1) return args.Values.First()?.ToString() ?? "";
        if (args.TryGetValue("args", out var nested) && nested is Dictionary<string, object?> nestedDict)
        {
            return GetArg(nestedDict, primary, aliases);
        }
        return "";
    }

    public static string GetNamedArg(Dictionary<string, object?> args, string primary, params string[] aliases)
    {
        if (args.TryGetValue(primary, out var v) && v is not null) return v.ToString() ?? "";
        foreach (var alias in aliases)
        {
            if (args.TryGetValue(alias, out var av) && av is not null) return av.ToString() ?? "";
        }
        if (args.TryGetValue("args", out var nested) && nested is Dictionary<string, object?> nestedDict)
        {
            return GetNamedArg(nestedDict, primary, aliases);
        }
        return "";
    }

    public static bool LooksTrue(object? value, bool fallback = false)
    {
        if (value is null) return fallback;
        if (value is bool b) return b;
        var normalized = value.ToString()?.Trim().ToLowerInvariant() ?? "";
        return normalized is not ("" or "0" or "false" or "no" or "off");
    }
}
