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

        ["bluetooth_devices"] = new("bluetooth_devices", "Inspect Bluetooth adapter status and paired/nearby devices for this machine.",
            Obj(new() { ["action"] = Str("'list' (default) or 'status'.") })),

        ["send_sms"] = new("send_sms", "Send an SMS via Twilio (requires TWILIO_ACCOUNT_SID/TWILIO_AUTH_TOKEN/TWILIO_FROM_NUMBER — set them in Settings).",
            Obj(new()
            {
                ["to"] = Str("Destination phone number, E.164 format."),
                ["message"] = Str("The SMS body text."),
                ["from"] = Str("Override the configured Twilio from-number."),
                ["dry_run"] = Str("If true, preview without actually sending."),
            }, "to", "message")),

        ["github_pages_deploy"] = new("github_pages_deploy",
            "Create or update a GitHub Pages site (requires GITHUB_TOKEN — set it in Settings). Creates the repo if missing, pushes index.html, enables Pages.",
            Obj(new()
            {
                ["html"] = Str("The full HTML content to deploy as index.html."),
                ["repo"] = Str("Repo name to deploy to (default: sparkbyte-home)."),
                ["message"] = Str("Commit message for the deploy."),
            }, "html")),

        ["metamorph"] = new("metamorph",
            "Inspect this engine's own tool registry, reload previously-forged dynamic tools from disk, or restore one by name.",
            Obj(new()
            {
                ["action"] = Str("'inspect' (default), 'reload_dynamic_tools', or 'restore_tool'."),
                ["name"] = Str("Tool name — required for the 'restore_tool' action."),
            })),

        ["card_cruncher"] = new("card_cruncher",
            "Convert a SillyTavern/CharacterTavern character card (PNG or JSON) into a new JL Engine agent card file.",
            Obj(new()
            {
                ["card_path"] = Str("Path to the character card file (.png or .json)."),
                ["out_path"] = Str("Override the output path for the generated agent card."),
                ["dry_run"] = Str("If true, convert and preview without writing a file."),
            }, "card_path")),

        ["playwright_interact"] = new("playwright_interact",
            "Drive a headless browser: navigate and perform a sequence of actions (click/fill/type/press/wait/wait_for/select/read/evaluate/screenshot). " +
            "Requires 'playwright install chromium' to have been run once.",
            Obj(new()
            {
                ["url"] = Str("Initial URL to navigate to."),
                ["actions"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["description"] = "Sequence of {type, selector, value, timeout_ms} action objects.",
                    ["items"] = new Dictionary<string, object?> { ["type"] = "object" },
                },
            })),
    };

    public static IReadOnlyDictionary<string, ToolSchemaEntry> All() => BuiltinSchemas;
}
