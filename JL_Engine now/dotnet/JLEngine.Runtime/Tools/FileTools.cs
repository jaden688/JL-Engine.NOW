namespace JLEngine.Runtime.Tools;

/// <summary>Port of Tools.jl's file I/O tools plus `_agent_write_guard` — the
/// self-protection mechanism that stops the agent from overwriting its own
/// engine source or secrets (.env, Project.toml, engine source dirs) unless
/// explicitly overridden via an env var.</summary>
public static class AgentWriteGuard
{
    private static readonly string[] ProtectedFiles =
    [
        ".env", ".env.example", ".env.local",
        "Project.toml", "Manifest.toml", "appsettings.json", "appsettings.Development.json",
        "sparkbyte.jl", "a2a_server.jl", "a2a_billing.jl",
    ];

    private static readonly string[] ProtectedDirs = ["/BYTE/src/", "/mcp_server/", "/JulianMetaMorph/", "/JLEngine.Runtime/", "/JLEngine.Core/"];

    public static (bool Blocked, string Reason) Check(string path)
    {
        var allow = (Environment.GetEnvironmentVariable("SPARKBYTE_ALLOW_SOURCE_EDITS") ?? "false").Trim().ToLowerInvariant();
        if (allow is "1" or "true" or "yes" or "on" or "enabled") return (false, "");

        var norm = Path.GetFullPath(path).Replace('\\', '/');
        var baseName = Path.GetFileName(norm);

        if (ProtectedFiles.Contains(baseName))
        {
            return (true, $"{baseName} is a protected engine config/secret file (it holds things like " +
                "the API key) and is write-locked. Edit it by hand, or set " +
                "SPARKBYTE_ALLOW_SOURCE_EDITS=true to override.");
        }

        foreach (var d in ProtectedDirs)
        {
            if (norm.Contains(d))
            {
                return (true, $"ENGINE SOURCE LOCKED: {baseName} — the agent cannot modify the engine " +
                    "that runs it. Forge a new tool instead, or write to data/, logs/, or tmp/. " +
                    "Override: set SPARKBYTE_ALLOW_SOURCE_EDITS=true.");
            }
        }

        return (false, "");
    }
}

public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";

    public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var path = ToolArgs.GetArg(args, "path", "file", "filepath", "filename", "name");
        if (string.IsNullOrEmpty(path))
        {
            return Task.FromResult(new Dictionary<string, object?> { ["error"] = "Missing required argument: 'path'" });
        }
        try
        {
            return Task.FromResult(new Dictionary<string, object?> { ["result"] = File.ReadAllText(path) });
        }
        catch (Exception e)
        {
            return Task.FromResult(new Dictionary<string, object?> { ["error"] = e.Message });
        }
    }
}

public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";

    public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var path = ToolArgs.GetArg(args, "path", "file", "filepath", "filename", "name");
        var content = ToolArgs.GetArg(args, "content", "text", "body", "data");
        if (string.IsNullOrEmpty(path))
        {
            return Task.FromResult(new Dictionary<string, object?> { ["error"] = "Missing required argument: 'path'" });
        }

        var (blocked, reason) = AgentWriteGuard.Check(path);
        if (blocked)
        {
            return Task.FromResult(new Dictionary<string, object?> { ["error"] = $"\U0001F512 PROTECTED: {reason}" });
        }

        try
        {
            File.WriteAllText(path, content);
            return Task.FromResult(new Dictionary<string, object?> { ["result"] = "Success" });
        }
        catch (Exception e)
        {
            return Task.FromResult(new Dictionary<string, object?> { ["error"] = e.Message });
        }
    }
}

public sealed class ListFilesTool : ITool
{
    public string Name => "list_files";

    public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var path = ToolArgs.GetArg(args, "path", "file", "filepath", "filename", "name", "dir", "directory");
        if (string.IsNullOrEmpty(path)) path = ".";
        try
        {
            var entries = Directory.GetFileSystemEntries(path).Select(Path.GetFileName);
            return Task.FromResult(new Dictionary<string, object?> { ["result"] = string.Join("\n", entries) });
        }
        catch (Exception e)
        {
            return Task.FromResult(new Dictionary<string, object?> { ["error"] = e.Message });
        }
    }
}
