using System.Text.Json;
using JLEngine.Core.Config;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace JLEngine.Runtime.Tools;

/// <summary>
/// Port of Tools.jl's tool_forge_new_tool: lets the LLM write and register a
/// brand-new tool at runtime. Faithful to Julia's actual security posture —
/// unsandboxed code execution gated only by a denylist (not real sandboxing)
/// — per the confirmed "Roslyn scripting, same posture" decision.
///
/// Mechanism adaptation: Julia's `Core.eval` defines a global function
/// `tool_&lt;name&gt;(args)` looked up by symbol name. C# scripting doesn't
/// support that same "eval a named global function, look it up dynamically"
/// pattern as cleanly, so the forged code's *convention* here is that it must
/// be (or evaluate to) a `Func&lt;Dictionary&lt;string,object?&gt;,
/// Dictionary&lt;string,object?&gt;&gt;` lambda — the delegate itself becomes
/// the registered tool. This preserves the capability (LLM-authored code
/// becomes a live callable tool, same denylist, same lack of sandboxing)
/// while adapting the mechanism to how C# scripting actually works.
/// </summary>
public sealed class ForgeNewToolTool(ToolRegistry registry) : ITool
{
    public string Name => "forge_new_tool";

    private static readonly (string Pattern, string Label)[] Denylist =
    [
        ("GetEnvironmentVariable(\"OPENAI_API_KEY", "key exfiltration"),
        ("GetEnvironmentVariable(\"GEMINI_API_KEY", "key exfiltration"),
        ("GetEnvironmentVariable(\"XAI_API_KEY", "key exfiltration"),
        ("GetEnvironmentVariable(\"CEREBRAS_API_KEY", "key exfiltration"),
        ("GetEnvironmentVariable(\"OPENROUTER_API_KEY", "key exfiltration"),
        ("GetEnvironmentVariable(\"AZURE_AI_API_KEY", "key exfiltration"),
        ("GetEnvironmentVariable(\"STRIPE_", "stripe key exfiltration"),
        ("GetEnvironmentVariable(\"A2A_ADMIN", "admin key exfiltration"),
        ("SetBrainBackendId", "backend self-selection (dropdown is authoritative)"),
        ("SetToolBackendId", "backend self-selection (dropdown is authoritative)"),
        ("SetBackendModel", "model self-selection (dropdown is authoritative)"),
    ];

    private static readonly (string Pattern, string Label)[] PhantomCapabilities =
    [
        ("microphone", "microphone / audio input"),
        ("camera", "camera / webcam"),
        ("Gpio", "GPIO / hardware serial"),
        ("SmtpClient", "email sending (no SMTP configured)"),
        ("CUDA", "GPU / CUDA (not available)"),
        ("NFC", "NFC / biometric hardware"),
    ];

    private static bool ForgeEnabled() =>
        !((Environment.GetEnvironmentVariable("SPARKBYTE_DISABLE_FORGE") ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on");

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        try
        {
            if (args.GetOr("name") is not string name || string.IsNullOrWhiteSpace(name))
            {
                return new Dictionary<string, object?> { ["error"] = "Missing required argument: 'name'" };
            }
            if (args.GetOr("code") is not string code || string.IsNullOrWhiteSpace(code))
            {
                return new Dictionary<string, object?> { ["error"] = "Missing required argument: 'code'" };
            }
            var description = args.GetOr("description") as string ?? $"Dynamically forged tool: {name}";
            var parameters = args.GetOr("parameters") as Dictionary<string, object?> ?? new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(),
                ["required"] = new List<object?>(),
            };

            if (!ForgeEnabled())
            {
                return new Dictionary<string, object?> { ["error"] = "FORGE DISABLED — SPARKBYTE_DISABLE_FORGE is set on this server." };
            }

            var blocked = Denylist.Where(d => code.Contains(d.Pattern)).Select(d => d.Label).ToList();
            if (blocked.Count > 0)
            {
                return new Dictionary<string, object?>
                {
                    ["error"] = $"FORGE REJECTED — forbidden pattern: {string.Join(", ", blocked)}. " +
                        "Forge is allowed for new tool functions only, not for secret exfiltration or model-lock tampering.",
                };
            }

            var hwViolations = PhantomCapabilities.Where(p => code.Contains(p.Pattern, StringComparison.OrdinalIgnoreCase)).Select(p => p.Label).ToList();
            if (hwViolations.Count > 0)
            {
                return new Dictionary<string, object?>
                {
                    ["error"] = $"FORGE REJECTED — hardware you cannot access: {string.Join(", ", hwViolations)}. " +
                        "Do not fake hardware capabilities. Return a real error if the device isn't available.",
                };
            }

            Func<Dictionary<string, object?>, Dictionary<string, object?>> compiled;
            try
            {
                compiled = await CSharpScript.EvaluateAsync<Func<Dictionary<string, object?>, Dictionary<string, object?>>>(code, ScriptDefaults.Options);
            }
            catch (CompilationErrorException e)
            {
                return new Dictionary<string, object?> { ["error"] = $"FORGE REJECTED — compile failure: {string.Join("; ", e.Diagnostics)}", ["stage"] = "compile" };
            }

            if (compiled is null)
            {
                return new Dictionary<string, object?>
                {
                    ["error"] = "Eval succeeded but the code did not evaluate to a Func<Dictionary<string,object?>,Dictionary<string,object?>> lambda.",
                };
            }

            var schema = new ToolSchemaEntry(name, description, parameters, true);
            var tool = new DelegateTool(name, compiled);

            // Smoke-test with synthesized dummy args before committing, same as Julia.
            var liveArgs = SynthesizeDummyArgs(parameters);
            Dictionary<string, object?> liveResult;
            try
            {
                liveResult = compiled(liveArgs);
            }
            catch (Exception e)
            {
                liveResult = new Dictionary<string, object?> { ["error"] = e.Message };
            }
            var liveOk = !liveResult.ContainsKey("error");

            if (!liveOk)
            {
                return new Dictionary<string, object?>
                {
                    ["error"] = $"Tool '{name}' forged but failed live test: {liveResult.GetOr("error")}",
                    ["forge_broken"] = true,
                    ["tool_name"] = name,
                    ["hint"] = "Fix the code and re-forge.",
                    ["live_result"] = liveResult,
                };
            }

            registry.RegisterDynamic(name, tool, schema);
            await PersistAsync(name, code, schema);

            return new Dictionary<string, object?> { ["result"] = $"Tool '{name}' is LIVE. Eval succeeded — registered in dispatch, smoke test passed." };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["error"] = $"Forge failed: {e.Message}" };
        }
    }

    /// <summary>Port of `_load_dynamic_tools!`'s reload path: re-compiles and
    /// re-registers a previously-forged tool from disk at boot, WITHOUT
    /// re-running the denylist/smoke-test gate — matching Julia, which only
    /// re-evals and re-registers on reload, it doesn't re-validate.</summary>
    public static async Task<bool> ReforgeFromDiskAsync(ToolRegistry registry, string name, string code, ToolSchemaEntry schema)
    {
        try
        {
            var compiled = await CSharpScript.EvaluateAsync<Func<Dictionary<string, object?>, Dictionary<string, object?>>>(code, ScriptDefaults.Options);
            if (compiled is null) return false;
            registry.RegisterDynamic(name, new DelegateTool(name, compiled), schema);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object?> SynthesizeDummyArgs(Dictionary<string, object?> parameters)
    {
        var live = new Dictionary<string, object?>();
        if (parameters.GetOr("required") is not List<object?> required) return live;
        var properties = parameters.GetOr("properties") as Dictionary<string, object?> ?? [];

        foreach (var req in required.OfType<string>())
        {
            var prop = properties.GetOr(req) as Dictionary<string, object?>;
            var type = (prop?.GetOr("type") as string ?? "string").ToLowerInvariant();
            live[req] = type switch
            {
                "integer" or "number" => 0,
                "boolean" => false,
                _ => "test",
            };
        }
        return live;
    }

    private async Task PersistAsync(string name, string code, ToolSchemaEntry schema)
    {
        var registryPath = Path.Combine(registry.StateDir, "dynamic_tools_registry.json");
        var sourcePath = Path.Combine(registry.StateDir, "dynamic_tools_source.json");

        var registryEntries = File.Exists(registryPath)
            ? JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(await File.ReadAllTextAsync(registryPath)) ?? []
            : [];
        registryEntries.RemoveAll(e => e.GetOr("name") as string == name);
        registryEntries.Add(new Dictionary<string, object?> { ["name"] = name, ["description"] = schema.Description, ["parameters"] = schema.Parameters });
        await File.WriteAllTextAsync(registryPath, JsonSerializer.Serialize(registryEntries, new JsonSerializerOptions { WriteIndented = true }));

        var sources = File.Exists(sourcePath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(sourcePath)) ?? []
            : [];
        sources[name] = code;
        await File.WriteAllTextAsync(sourcePath, JsonSerializer.Serialize(sources, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Wraps a compiled forged-tool delegate as an ITool for uniform dispatch.</summary>
public sealed class DelegateTool(string name, Func<Dictionary<string, object?>, Dictionary<string, object?>> fn) : ITool
{
    public string Name => name;
    public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args) => Task.FromResult(fn(args));
}
