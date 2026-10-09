using System.Net;
using System.Text;
using System.Text.Json;

namespace JLEngine.Runtime.Tools;

/// <summary>Port of Tools.jl's tool_bluetooth_devices — per-OS shell-outs
/// (Windows: PnP/service registry via PowerShell; macOS: system_profiler;
/// Linux: bluetoothctl), matching the exact command set Julia used.</summary>
public sealed class BluetoothDevicesTool : ITool
{
    public string Name => "bluetooth_devices";

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var rawAction = ToolArgs.GetArg(args, "action");
        var action = string.IsNullOrEmpty(rawAction) ? "list" : rawAction.Trim().ToLowerInvariant();
        if (action is not ("list" or "status"))
        {
            return new Dictionary<string, object?> { ["error"] = $"Unsupported action '{action}'. Use 'list' or 'status'." };
        }

        if (OperatingSystem.IsWindows())
        {
            var (svcOk, svcOut, _) = await ShellRunner.RunAsync(
                "Get-Service bthserv -ErrorAction SilentlyContinue | Select-Object Status,StartType,Name | ConvertTo-Json -Compress");
            var (devOk, devOut, _) = await ShellRunner.RunAsync(
                "Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue | Select-Object Status,Class,FriendlyName,InstanceId | ConvertTo-Json -Depth 4 -Compress");
            return new Dictionary<string, object?>
            {
                ["platform"] = "windows",
                ["action"] = action,
                ["service"] = svcOk && !string.IsNullOrWhiteSpace(svcOut) ? svcOut : "Unavailable",
                ["devices"] = devOk && !string.IsNullOrWhiteSpace(devOut) ? devOut : "Unavailable",
                ["result"] = "Bluetooth status collected from Windows service and device registry.",
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            var (ok, output, error) = await ShellRunner.RunAsync("system_profiler SPBluetoothDataType -json");
            if (!ok) return new Dictionary<string, object?> { ["error"] = $"Bluetooth inspection failed: {error ?? "unknown error"}" };
            return new Dictionary<string, object?>
            {
                ["platform"] = "macos",
                ["action"] = action,
                ["profile"] = output,
                ["result"] = "Bluetooth profile collected from system_profiler.",
            };
        }

        if (OperatingSystem.IsLinux())
        {
            var (showOk, showOut, showErr) = await ShellRunner.RunAsync("bluetoothctl show");
            var listCmd = action == "status" ? "bluetoothctl paired-devices" : "bluetoothctl devices";
            var (listOk, listOut, listErr) = await ShellRunner.RunAsync(listCmd);
            return new Dictionary<string, object?>
            {
                ["platform"] = "linux",
                ["action"] = action,
                ["adapter"] = showOk ? showOut : (showErr ?? "Unavailable"),
                ["devices"] = listOk ? listOut : (listErr ?? "Unavailable"),
                ["result"] = "Bluetooth information collected from bluetoothctl.",
            };
        }

        return new Dictionary<string, object?> { ["error"] = $"Bluetooth inspection is not implemented for {Environment.OSVersion.Platform}." };
    }
}

/// <summary>Port of Tools.jl's tool_send_sms — Twilio REST API. Reads
/// TWILIO_ACCOUNT_SID/TWILIO_AUTH_TOKEN/TWILIO_FROM_NUMBER (settable via the
/// GUI's Settings panel), matching Julia's env-var-driven configuration and
/// dry_run preview behavior exactly.</summary>
public sealed class SendSmsTool(HttpClient http) : ITool
{
    public string Name => "send_sms";

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var rawProvider = ToolArgs.GetArg(args, "provider");
        var provider = string.IsNullOrEmpty(rawProvider) ? "twilio" : rawProvider.Trim().ToLowerInvariant();
        if (provider != "twilio")
        {
            return new Dictionary<string, object?> { ["error"] = $"Unsupported SMS provider '{provider}'. Only 'twilio' is implemented right now." };
        }

        var to = ToolArgs.GetArg(args, "to");
        var body = ToolArgs.GetArg(args, "message", "body");
        var fromOverride = ToolArgs.GetArg(args, "from");
        var dryRun = args.TryGetValue("dry_run", out var dr) && ToolArgs.LooksTrue(dr);

        if (string.IsNullOrWhiteSpace(to)) return new Dictionary<string, object?> { ["error"] = "Missing required field: to" };
        if (string.IsNullOrWhiteSpace(body)) return new Dictionary<string, object?> { ["error"] = "Missing required field: message" };

        var sid = Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID") ?? "";
        var token = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN") ?? "";
        var from = string.IsNullOrEmpty(fromOverride) ? Environment.GetEnvironmentVariable("TWILIO_FROM_NUMBER") ?? "" : fromOverride;

        var missing = new List<string>();
        if (string.IsNullOrEmpty(sid)) missing.Add("TWILIO_ACCOUNT_SID");
        if (string.IsNullOrEmpty(token)) missing.Add("TWILIO_AUTH_TOKEN");
        if (string.IsNullOrEmpty(from)) missing.Add("TWILIO_FROM_NUMBER");

        var preview = new Dictionary<string, object?>
        {
            ["provider"] = provider,
            ["to"] = to,
            ["from"] = from,
            ["message_preview"] = body.Length > 160 ? body[..160] : body,
            ["configured"] = missing.Count == 0,
        };

        if (dryRun)
        {
            preview["result"] = "SMS dry run only. No message was sent.";
            return preview;
        }
        if (missing.Count > 0)
        {
            return new Dictionary<string, object?>
            {
                ["error"] = $"Twilio SMS is not configured. Missing: {string.Join(", ", missing)}",
                ["missing_env"] = missing,
            };
        }

        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["To"] = to, ["From"] = from, ["Body"] = body });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.twilio.com/2010-04-01/Accounts/{sid}/Messages.json") { Content = form };
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sid}:{token}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);

            var response = await http.SendAsync(request);
            var bodyText = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(bodyText);
                var root = doc.RootElement;
                preview["result"] = "SMS request accepted by Twilio.";
                preview["status"] = root.TryGetProperty("status", out var s) ? s.GetString() : "";
                preview["sid"] = root.TryGetProperty("sid", out var sidProp) ? sidProp.GetString() : "";
                return preview;
            }
            return new Dictionary<string, object?>
            {
                ["error"] = $"Twilio rejected the SMS request with HTTP {(int)response.StatusCode}.",
                ["details"] = bodyText.Length > 500 ? bodyText[..500] : bodyText,
            };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["error"] = $"SMS send failed: {e.Message}" };
        }
    }
}

/// <summary>Port of Tools.jl's tool_github_pages_deploy — creates/updates a
/// GitHub Pages site via the GitHub REST API: ensures the repo exists, pushes
/// index.html, enables Pages on main. Uses GITHUB_TOKEN (settable via the
/// GUI's Settings panel).</summary>
public sealed class GitHubPagesDeployTool(HttpClient http) : ITool
{
    public string Name => "github_pages_deploy";

    private static HttpRequestMessage Req(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.UserAgent.ParseAdd("SparkByte-JLEngine/1.0");
        return req;
    }

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var explicitToken = ToolArgs.GetArg(args, "token");
        var token = string.IsNullOrEmpty(explicitToken) ? Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "" : explicitToken;
        if (string.IsNullOrEmpty(token))
        {
            return new Dictionary<string, object?> { ["error"] = "No GITHUB_TOKEN found. Set it in Settings or pass as 'token'." };
        }

        var rawRepo = ToolArgs.GetArg(args, "repo");
        var repoName = string.IsNullOrEmpty(rawRepo) ? "sparkbyte-home" : rawRepo;
        var html = ToolArgs.GetArg(args, "html");
        var rawMessage = ToolArgs.GetArg(args, "message");
        var commitMsg = string.IsNullOrEmpty(rawMessage) ? "SparkByte auto-deploy" : rawMessage;
        if (string.IsNullOrEmpty(html)) return new Dictionary<string, object?> { ["error"] = "Provide 'html' content to deploy." };

        try
        {
            using var userResp = await http.SendAsync(Req(HttpMethod.Get, "https://api.github.com/user", token));
            var userBody = await userResp.Content.ReadAsStringAsync();
            using var userDoc = JsonDocument.Parse(userBody);
            var username = userDoc.RootElement.TryGetProperty("login", out var loginProp) ? loginProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(username)) return new Dictionary<string, object?> { ["error"] = "Could not get GitHub username from token." };

            var repoUrl = $"https://api.github.com/repos/{username}/{repoName}";
            using var repoResp = await http.SendAsync(Req(HttpMethod.Get, repoUrl, token));
            if (repoResp.StatusCode == HttpStatusCode.NotFound)
            {
                var createPayload = JsonSerializer.Serialize(new
                {
                    name = repoName,
                    description = "SparkByte — JL Engine live demo",
                    homepage = $"https://{username}.github.io/{repoName}",
                    auto_init = true,
                    @private = false,
                });
                var createReq = Req(HttpMethod.Post, "https://api.github.com/user/repos", token);
                createReq.Content = new StringContent(createPayload, Encoding.UTF8, "application/json");
                using var createResp = await http.SendAsync(createReq);
                if (!createResp.IsSuccessStatusCode)
                {
                    var body = await createResp.Content.ReadAsStringAsync();
                    return new Dictionary<string, object?>
                    {
                        ["error"] = $"Failed to create repo: HTTP {(int)createResp.StatusCode}",
                        ["body"] = body.Length > 300 ? body[..300] : body,
                    };
                }
                await Task.Delay(2000); // GitHub needs a moment after creation
            }

            var fileUrl = $"https://api.github.com/repos/{username}/{repoName}/contents/index.html";
            using var fileResp = await http.SendAsync(Req(HttpMethod.Get, fileUrl, token));
            var sha = "";
            if (fileResp.StatusCode == HttpStatusCode.OK)
            {
                var fileBody = await fileResp.Content.ReadAsStringAsync();
                using var fileDoc = JsonDocument.Parse(fileBody);
                sha = fileDoc.RootElement.TryGetProperty("sha", out var shaProp) ? shaProp.GetString() ?? "" : "";
            }

            var putPayload = new Dictionary<string, object?>
            {
                ["message"] = commitMsg,
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(html)),
            };
            if (!string.IsNullOrEmpty(sha)) putPayload["sha"] = sha;
            var putReq = Req(HttpMethod.Put, fileUrl, token);
            putReq.Content = new StringContent(JsonSerializer.Serialize(putPayload), Encoding.UTF8, "application/json");
            using var putResp = await http.SendAsync(putReq);
            if (!putResp.IsSuccessStatusCode)
            {
                var body = await putResp.Content.ReadAsStringAsync();
                return new Dictionary<string, object?>
                {
                    ["error"] = $"Failed to push index.html: HTTP {(int)putResp.StatusCode}",
                    ["body"] = body.Length > 300 ? body[..300] : body,
                };
            }

            var pagesUrl = $"https://api.github.com/repos/{username}/{repoName}/pages";
            using var pagesResp = await http.SendAsync(Req(HttpMethod.Get, pagesUrl, token));
            if (pagesResp.StatusCode == HttpStatusCode.NotFound)
            {
                var enableReq = Req(HttpMethod.Post, pagesUrl, token);
                enableReq.Content = new StringContent(
                    JsonSerializer.Serialize(new { source = new { branch = "main", path = "/" } }), Encoding.UTF8, "application/json");
                try { await http.SendAsync(enableReq); } catch { /* best-effort; deploy above already succeeded */ }
            }

            var liveUrl = $"https://{username}.github.io/{repoName}";
            return new Dictionary<string, object?>
            {
                ["result"] = "Deployed to GitHub Pages.",
                ["live_url"] = liveUrl,
                ["repo"] = $"https://github.com/{username}/{repoName}",
                ["username"] = username,
                ["note"] = "Pages may take 1-2 minutes to go live on first deploy.",
            };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["error"] = e.Message };
        }
    }
}

/// <summary>Port of Tools.jl's tool_metamorph — introspection and dynamic-
/// tool-registry management. Julia's "curiosity_hunt"/"hunt_task" actions
/// (which shell out to a separate `julian_metamorph` Python CLI) aren't
/// ported: they're tied to a Python package with no C# equivalent, not a
/// mechanical port. inspect / reload_dynamic_tools / restore_tool — the
/// actions grounded in this port's own ToolRegistry — are fully real.</summary>
public sealed class MetamorphTool(ToolRegistry tools, IReadOnlyDictionary<string, ToolSchemaEntry> builtinSchemas) : ITool
{
    public string Name => "metamorph";

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var rawAction = ToolArgs.GetArg(args, "action");
        var action = string.IsNullOrEmpty(rawAction) ? "inspect" : rawAction;

        switch (action)
        {
            case "inspect":
            {
                var liveTools = tools.ToolNames().OrderBy(n => n, StringComparer.Ordinal).ToList();
                var dynamicNames = tools.DynamicSchema.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
                var missingStatic = builtinSchemas.Keys.Where(n => !tools.Contains(n)).ToList();
                return new Dictionary<string, object?>
                {
                    ["live_tools"] = liveTools,
                    ["dynamic_tools"] = dynamicNames,
                    ["missing_static"] = missingStatic,
                    ["tool_count"] = liveTools.Count,
                    ["dynamic_count"] = dynamicNames.Count,
                    ["status"] = missingStatic.Count == 0 ? "healthy" : $"degraded — missing: {string.Join(", ", missingStatic)}",
                };
            }

            case "reload_dynamic_tools":
            {
                var before = tools.ToolNames().Count;
                try
                {
                    await tools.LoadPersistedToolsAsync((name, code, schema) => ForgeNewToolTool.ReforgeFromDiskAsync(tools, name, code, schema));
                }
                catch (Exception e)
                {
                    return new Dictionary<string, object?> { ["error"] = $"reload failed: {e.Message}" };
                }
                var after = tools.ToolNames().Count;
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["tools_before"] = before,
                    ["tools_after"] = after,
                    ["added"] = after - before,
                    ["dynamic_tools"] = tools.DynamicSchema.Keys.ToList(),
                };
            }

            case "restore_tool":
            {
                var name = ToolArgs.GetArg(args, "name");
                if (string.IsNullOrEmpty(name)) return new Dictionary<string, object?> { ["error"] = "'name' required for restore_tool action" };

                var registryPath = Path.Combine(tools.StateDir, "dynamic_tools_registry.json");
                var sourcePath = Path.Combine(tools.StateDir, "dynamic_tools_source.json");
                if (!File.Exists(registryPath) || !File.Exists(sourcePath))
                {
                    return new Dictionary<string, object?> { ["error"] = $"No persisted dynamic tool named '{name}' found." };
                }

                using var registryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(registryPath));
                using var sourceDoc = JsonDocument.Parse(await File.ReadAllTextAsync(sourcePath));
                var sources = sourceDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

                foreach (var entry in registryDoc.RootElement.EnumerateArray())
                {
                    if (entry.GetProperty("name").GetString() != name) continue;
                    if (!sources.TryGetValue(name, out var code)) break;

                    var description = entry.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var parameters = entry.TryGetProperty("parameters", out var p)
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(p.GetRawText()) ?? []
                        : [];

                    await ForgeNewToolTool.ReforgeFromDiskAsync(tools, name, code, new ToolSchemaEntry(name, description, parameters, true));
                    return new Dictionary<string, object?> { ["ok"] = true, ["restored"] = name, ["restored_from"] = sourcePath };
                }

                return new Dictionary<string, object?> { ["error"] = $"No persisted dynamic tool named '{name}' found." };
            }

            default:
                return new Dictionary<string, object?>
                {
                    ["error"] = $"Unknown metamorph action: '{action}'. Use 'inspect', 'reload_dynamic_tools', or 'restore_tool'.",
                };
        }
    }
}
