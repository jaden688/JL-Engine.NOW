namespace JLEngine.Runtime.Tools;

/// <summary>OpenAI-format (lowercase types) schema declarations for the
/// built-in tools, replacing Schema.jl's Gemini-shaped TOOLS_SCHEMA — the
/// wire format every live provider (OpenRouter, Ollama) actually uses, per
/// the plan's decision to skip the Gemini representation entirely.</summary>
public static class ToolSchemas
{
    private static Dictionary<string, object?> Obj(Dictionary<string, object?> properties, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = required.ToList(),
    };

    private static Dictionary<string, object?> Str(string description) => new() { ["type"] = "string", ["description"] = description };

    public static IReadOnlyDictionary<string, ToolSchemaEntry> BuiltinSchemas { get; } = new Dictionary<string, ToolSchemaEntry>
    {
        ["read_file"] = new("read_file", "Read the full contents of a file at the given path.",
            Obj(new() { ["path"] = Str("Path to the file to read.") }, "path")),

        ["write_file"] = new("write_file", "Write content to a file at the given path, creating or overwriting it.",
            Obj(new() { ["path"] = Str("Path to the file to write."), ["content"] = Str("Content to write.") }, "path", "content")),

        ["list_files"] = new("list_files", "List files and directories at the given path (defaults to the current directory).",
            Obj(new() { ["path"] = Str("Directory to list.") })),

        ["run_command"] = new("run_command", "Run a shell command and return its combined stdout/stderr.",
            Obj(new() { ["command"] = Str("The shell command to run.") }, "command")),

        ["get_os_info"] = new("get_os_info", "Get basic OS/CPU/.NET runtime information.", Obj([])),

        ["execute_code"] = new("execute_code", "Execute a snippet of C# or Python code and return its output.",
            Obj(new() { ["code"] = Str("The code to execute."), ["language"] = Str("'csharp' or 'python'; inferred from the code if omitted.") }, "code")),

        ["forge_new_tool"] = new("forge_new_tool",
            "Forge a brand-new tool at runtime. `code` must evaluate to a C# lambda of type " +
            "Func<Dictionary<string,object?>, Dictionary<string,object?>>.",
            Obj(new()
            {
                ["name"] = Str("The new tool's name."),
                ["code"] = Str("C# script source evaluating to a Func<Dictionary<string,object?>,Dictionary<string,object?>> lambda."),
                ["description"] = Str("Description of what the tool does."),
                ["parameters"] = new Dictionary<string, object?> { ["type"] = "object", ["description"] = "JSON schema (OpenAI format) for the tool's arguments." },
            }, "name", "code")),

        ["browse_url"] = new("browse_url", "Fetch a URL over HTTP and return its text content (tags stripped).",
            Obj(new() { ["url"] = Str("The URL to fetch.") }, "url")),

        ["github_pillage"] = new("github_pillage", "Fetch a GitHub file's raw contents given a blob URL or raw URL.",
            Obj(new() { ["url"] = Str("A github.com blob URL or raw.githubusercontent.com URL.") }, "url")),

        ["discord_webhook"] = new("discord_webhook", "Post a message to a configured Discord webhook.",
            Obj(new() { ["message"] = Str("The message content to post."), ["webhook_url"] = Str("Override the configured webhook URL.") }, "message")),

        ["remember"] = new("remember", "Store a note in long-term breadcrumb memory, optionally tagged with an intent/topic.",
            Obj(new() { ["note"] = Str("The note to remember."), ["intent"] = Str("Optional topic/intent tag.") }, "note")),

        ["recall"] = new("recall", "Recall previously remembered notes, optionally filtered by intent/topic.",
            Obj(new() { ["intent"] = Str("Optional topic/intent filter."), ["limit"] = Str("Max number of results.") })),
    };

    /// <summary>Minimal placeholder schemas for the 6 not-ported tools, so
    /// they remain visible in the tool list (matching Julia's complete
    /// TOOLS_SCHEMA) even though dispatch returns a not-implemented error.</summary>
    public static IReadOnlyDictionary<string, ToolSchemaEntry> StubSchemas { get; } = NotPortedTool.All()
        .ToDictionary(t => t.Name, t => new ToolSchemaEntry(t.Name, $"(Not implemented in the C# port yet) {t.Name}", Obj([])));

    public static IReadOnlyDictionary<string, ToolSchemaEntry> All() =>
        BuiltinSchemas.Concat(StubSchemas).ToDictionary(kv => kv.Key, kv => kv.Value);
}
