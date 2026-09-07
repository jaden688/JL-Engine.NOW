using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JLEngine.Core.Engine;
using JLEngine.Core.Investment;
using JLEngine.Core.Types;
using JLEngine.Persistence;
using JLEngine.Runtime.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace JLEngine.Runtime;

public sealed record SetModelRequest(string SessionId, string Model);
public sealed record SetAgentRequest(string SessionId, string Name);
public sealed record SetToolRequest(string SessionId, string Name, bool Enabled);
public sealed record SetBehaviorRequest(string SessionId, bool? SafetyOn, int? MaxToolLoops, int? HistoryLength);
public sealed record SetCredentialRequest(string Name, string Value);
public sealed record ForgeToolRequest(string SessionId, string Name, string Code, string? Description, Dictionary<string, object?>? Parameters);
public sealed record DeleteToolRequest(string SessionId, string Name);
public sealed record RunToolRequest(string SessionId, string Name, Dictionary<string, object?>? Args);
public sealed record ChatGptTurnRequest(string Message, string? SessionId, string Mode);

/// <summary>Process-wide resources one JL Engine process needs regardless of
/// how many chat sessions (GUI tabs) are open against it: persistence,
/// telemetry, and the shared HTTP client. Exactly one of these exists per
/// process — unlike JLEngineCore/ToolRegistry/AgentRuntime, which are
/// per-session (see ChatSession) so each tab gets its own operator/model/
/// conversation without the others' state bleeding through.</summary>
public sealed record SharedComponents(
    string ProjectRoot,
    string StateDir,
    Telemetry Telemetry,
    SparkByteDatabase Db,
    HttpClient Http);

/// <summary>One independent conversation: its own engine (agent/gait/rhythm/
/// aperture state), its own tool registry (so per-session tool enable/disable
/// doesn't affect other tabs), its own model choice, and its own message
/// history. Telemetry/Db/Http are shared (see SharedComponents) — matching a
/// single log/memory-of-record even though the "current agent" is no longer
/// truly global like it is in the real Julia app.</summary>
public sealed class ChatSession
{
    public required string Id { get; init; }
    public required JLEngineCore Engine { get; init; }
    public required ToolRegistry Tools { get; init; }
    public required AgentRuntime Runtime { get; init; }
    public List<Dictionary<string, object?>> History { get; } = [];
    public SemaphoreSlim TurnGate { get; } = new(1, 1);
    public List<AgentToolActivity> RecentToolActivity { get; } = [];
}

/// <summary>Lazily creates and caches one ChatSession per client-generated
/// session ID. Uses Task-caching GetOrAdd so concurrent requests for a
/// brand-new session id await the same in-flight build rather than racing
/// to construct two independent engines for it.</summary>
public sealed class SessionRegistry(SharedComponents shared, Action<ChatSession>? onSessionCreated = null)
{
    private readonly ConcurrentDictionary<string, Task<ChatSession>> _sessions = new();

    public Task<ChatSession> GetOrCreateAsync(string sessionId) =>
        _sessions.GetOrAdd(sessionId, id => CreateAndHookAsync(id));

    private async Task<ChatSession> CreateAndHookAsync(string sessionId)
    {
        var chat = await RuntimeComposition.CreateSessionAsync(shared, sessionId);
        // Lets JLEngine.Host register tools that depend on JLEngine.Bridges
        // (e.g. card_cruncher) into every new session — Runtime can't
        // reference Bridges directly (Bridges already depends on Runtime).
        onSessionCreated?.Invoke(chat);
        return chat;
    }

    public IReadOnlyCollection<string> ActiveIds => (IReadOnlyCollection<string>)_sessions.Keys;
}

public static class RuntimeComposition
{
    public static async Task<SharedComponents> BuildSharedAsync(string projectRoot)
    {
        var stateDir = Path.Combine(projectRoot, "data");
        Directory.CreateDirectory(stateDir);

        // Full 10-table sparkbyte_memory.db schema (Phase 3) — one connection,
        // shared by every session's tool-usage/turn-snapshot writes.
        var db = new SparkByteDatabase(Path.Combine(stateDir, "sparkbyte_memory.db"));
        var telemetry = new Telemetry(projectRoot, db.Connection);
        db.StartSession(telemetry.SessionId, Environment.OSVersion.ToString(), Environment.Version.ToString());

        return new SharedComponents(projectRoot, stateDir, telemetry, db, new HttpClient());
    }

    public static async Task<ChatSession> CreateSessionAsync(SharedComponents shared, string sessionId)
    {
        var (projectRoot, stateDir, telemetry, db, http) = shared;

        var engine = new JLEngineCore(new EngineConfig { RootDir = projectRoot });
        var toolRegistry = new ToolRegistry(stateDir);

        toolRegistry.Register(new ReadFileTool());
        toolRegistry.Register(new WriteFileTool());
        toolRegistry.Register(new ListFilesTool());
        toolRegistry.Register(new RunCommandTool());
        toolRegistry.Register(new GetOsInfoTool());
        toolRegistry.Register(new ExecuteCodeTool());
        toolRegistry.Register(new ForgeNewToolTool(toolRegistry));
        toolRegistry.Register(new BrowseUrlTool(http));
        toolRegistry.Register(new GithubPillageTool(http));
        toolRegistry.Register(new DiscordWebhookTool(http));
        toolRegistry.Register(new RememberTool(engine.MemorySystem, engine.CurrentAgentName));
        toolRegistry.Register(new RecallTool(engine.MemorySystem));
        toolRegistry.Register(new BluetoothDevicesTool());
        toolRegistry.Register(new SendSmsTool(http));
        toolRegistry.Register(new GitHubPagesDeployTool(http));
        toolRegistry.Register(new MetamorphTool(toolRegistry, ToolSchemas.BuiltinSchemas));
        toolRegistry.Register(new PlaywrightInteractTool());
        // card_cruncher isn't registered here: it needs JLEngine.Bridges'
        // CardCruncher, which Runtime can't reference (Bridges depends on
        // Runtime, not the reverse) — JLEngine.Host registers it via the
        // SessionRegistry onSessionCreated hook instead.

        // Port of init_tools's tools-table sync: upsert one row per declared
        // schema, preserving any existing call_count. Idempotent, so
        // re-running it for every new session is harmless.
        foreach (var schema in ToolSchemas.BuiltinSchemas.Values)
        {
            db.UpsertToolSchema(schema.Name, "builtin", schema.Description, JsonSerializer.Serialize(schema.Parameters), false);
        }

        toolRegistry.OnToolUsage = (name, args, result, elapsedMs) =>
        {
            db.WriteToolUsage(name, JsonSerializer.Serialize(args), JsonSerializer.Serialize(result), elapsedMs, engine.CurrentAgentName, telemetry.SessionId);
            telemetry.LogEvent("tool_usage_log", new Dictionary<string, object?>
            {
                ["tool_name"] = name,
                ["elapsed_ms"] = elapsedMs,
                ["is_error"] = result.ContainsKey("error"),
            });
        };

        // Reload any previously-forged tools from disk (mirrors _load_dynamic_tools!).
        // Forged tools live in the shared StateDir, so every session sees tools
        // forged before it was created; a tool forged in an already-open tab
        // won't retroactively appear in other already-open tabs until reopened.
        await toolRegistry.LoadPersistedToolsAsync((name, code, schema) =>
            ForgeNewToolTool.ReforgeFromDiskAsync(toolRegistry, name, code, schema));
        foreach (var schema in toolRegistry.DynamicSchema.Values)
        {
            db.UpsertToolSchema(schema.Name, "dynamic", schema.Description, JsonSerializer.Serialize(schema.Parameters), true);
        }

        var agentRuntime = new AgentRuntime(engine, toolRegistry, telemetry, http, ToolSchemas.All(), db) { SessionId = sessionId };
        if (string.Equals(sessionId, "chatgpt-private", StringComparison.Ordinal))
        {
            agentRuntime.Provider = ModelProvider.OpenAI;
            agentRuntime.CurrentModel = Environment.GetEnvironmentVariable("CHATGPT_JL_MODEL")?.Trim() switch
            {
                { Length: > 0 } configured => configured,
                _ => "gpt-5-nano",
            };
        }
        return new ChatSession { Id = sessionId, Engine = engine, Tools = toolRegistry, Runtime = agentRuntime };
    }

    /// <summary>Wires every HTTP/WS endpoint the GUI (and A2A/Autopilot,
    /// via their own fixed session) needs onto a WebApplication — shared
    /// between Runtime's standalone Program.cs and JLEngine.Host.</summary>
    public static void MapChatEndpoints(this WebApplication app, SharedComponents shared, SessionRegistry sessions)
    {
        var telemetry = shared.Telemetry;
        var db = shared.Db;
        var eventCount = 0;
        app.Lifetime.ApplicationStopping.Register(() => db.EndSession(telemetry.SessionId, eventCount));

        app.UseWebSockets();
        app.MapGet("/health", () => Results.Ok(new { status = "ok", sessions = sessions.ActiveIds.Count }));

        // Cognitive-state snapshot for one session's status bar — read-only,
        // reflects whatever that session's engine was left at after its last turn.
        app.MapGet("/api/state", async (string session) =>
        {
            var chat = await sessions.GetOrCreateAsync(session);
            var apertureState = chat.Engine.EmotionalAperture.LastState;
            return Results.Ok(new
            {
                agent = chat.Engine.CurrentAgentName,
                gait = chat.Engine.CurrentGait,
                rhythmMode = chat.Engine.CurrentRhythmMode,
                apertureMode = apertureState.Mode,
                emotion = apertureState.Emotion ?? "unknown",
                investmentGear = InvestmentSystem.InvestmentGear(chat.Engine.InvestmentSystem.Level),
                stability = chat.Engine.StabilityScore,
                model = chat.Runtime.CurrentModel,
            });
        });

        app.MapGet("/api/chatgpt/activity", async (string? session, int? limit) =>
        {
            var chat = await sessions.GetOrCreateAsync(NormalizeChatGptSession(session));
            var take = Math.Clamp(limit ?? 20, 1, 50);
            lock (chat.RecentToolActivity)
            {
                return Results.Ok(new { activity = chat.RecentToolActivity.TakeLast(take).ToList() });
            }
        });

        app.MapPost("/api/chatgpt/turn", async (ChatGptTurnRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new { error = "message is required" });
            if (req.Message.Length > 20_000)
                return Results.BadRequest(new { error = "message must be 20000 characters or fewer" });

            var mode = req.Mode.Trim().ToLowerInvariant();
            var policy = mode switch
            {
                "talk" => ToolExecutionPolicy.None,
                "execute" => ToolExecutionPolicy.Full,
                _ => (ToolExecutionPolicy?)null,
            };
            if (policy is null)
                return Results.BadRequest(new { error = "mode must be 'talk' or 'execute'" });

            var chat = await sessions.GetOrCreateAsync(NormalizeChatGptSession(req.SessionId));
            await chat.TurnGate.WaitAsync();
            try
            {
                var result = await chat.Runtime.ProcessMessageDetailedAsync(
                    req.Message,
                    chat.History,
                    chat.Runtime.CurrentModel,
                    policy.Value);
                AppendHistory(chat, req.Message, result.Reply);
                lock (chat.RecentToolActivity)
                {
                    chat.RecentToolActivity.AddRange(result.ToolActivity);
                    if (chat.RecentToolActivity.Count > 50)
                        chat.RecentToolActivity.RemoveRange(0, chat.RecentToolActivity.Count - 50);
                }

                var apertureState = chat.Engine.EmotionalAperture.LastState;
                return Results.Ok(new
                {
                    reply = result.Reply,
                    mode,
                    state = new
                    {
                        agent = chat.Engine.CurrentAgentName,
                        gait = chat.Engine.CurrentGait,
                        rhythmMode = chat.Engine.CurrentRhythmMode,
                        apertureMode = apertureState.Mode,
                        emotion = apertureState.Emotion ?? "unknown",
                        investmentGear = InvestmentSystem.InvestmentGear(chat.Engine.InvestmentSystem.Level),
                        stability = chat.Engine.StabilityScore,
                        model = chat.Runtime.CurrentModel,
                    },
                    toolActivity = result.ToolActivity,
                });
            }
            finally
            {
                chat.TurnGate.Release();
            }
        });

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var sessionId = context.Request.Query["session"].FirstOrDefault() ?? "default";
            var chat = await sessions.GetOrCreateAsync(sessionId);

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[1024 * 64];

            while (ws.State == WebSocketState.Open)
            {
                var received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                if (received.MessageType == WebSocketMessageType.Close) break;

                eventCount++;
                var raw = Encoding.UTF8.GetString(buffer, 0, received.Count);
                telemetry.LogWsIn("user_msg", raw.Length > 80 ? raw[..80] : raw);

                string userText;
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    userText = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : raw;
                }
                catch
                {
                    userText = raw;
                }

                await chat.TurnGate.WaitAsync(context.RequestAborted);
                string reply;
                try
                {
                    reply = await chat.Runtime.ProcessMessageAsync(userText, chat.History, chat.Runtime.CurrentModel);
                    AppendHistory(chat, userText, reply);
                }
                finally
                {
                    chat.TurnGate.Release();
                }
                // Enforce HistoryLength (turns = user+assistant pairs) — settable
                // via the GUI's settings panel; drop oldest pairs once over the cap.

                var replyJson = JsonSerializer.Serialize(new { type = "reply", text = reply });
                telemetry.LogWsOut("reply");
                await ws.SendAsync(Encoding.UTF8.GetBytes(replyJson), WebSocketMessageType.Text, true, context.RequestAborted);
            }
        });

        // --- Live-terminal feed: every telemetry event, pushed as it happens ---
        // Intentionally global (not per-session) — it's meant to show
        // everything the engine is doing across every open tab, not just one.
        app.Map("/ws/logs", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            // Telemetry.OnEvent fires from whatever thread is mid-turn; a bounded,
            // single-reader channel decouples that from this socket's send loop
            // (WebSocket.SendAsync isn't safe to call concurrently from multiple threads).
            var channel = Channel.CreateUnbounded<Dictionary<string, object?>>();
            void Handler(Dictionary<string, object?> e) => channel.Writer.TryWrite(e);
            telemetry.OnEvent += Handler;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var receiveDrain = Task.Run(async () =>
            {
                var drainBuffer = new byte[16];
                try
                {
                    while (ws.State == WebSocketState.Open)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(drainBuffer), cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                    }
                }
                catch { /* client gone */ }
                finally { cts.Cancel(); }
            });

            try
            {
                await foreach (var evt in channel.Reader.ReadAllAsync(cts.Token))
                {
                    var json = JsonSerializer.Serialize(evt);
                    await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cts.Token);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally
            {
                telemetry.OnEvent -= Handler;
                await receiveDrain;
            }
        });

        // --- Model selector: which OpenRouter model a session's chat loop calls ---
        app.MapGet("/api/model", async (string session) =>
        {
            var chat = await sessions.GetOrCreateAsync(session);
            return Results.Ok(new
            {
                model = chat.Runtime.CurrentModel,
                presets = new[]
                {
                    "gpt-5-nano",
                    "deepseek/deepseek-v4-flash",
                    "anthropic/claude-sonnet-4.5",
                    "openai/gpt-5",
                    "google/gemini-2.5-pro",
                    "x-ai/grok-4",
                },
            });
        });

        app.MapPost("/api/model", async (SetModelRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Model)) return Results.BadRequest(new { error = "model is required" });
            var chat = await sessions.GetOrCreateAsync(req.SessionId);
            var previous = chat.Runtime.CurrentModel;
            chat.Runtime.CurrentModel = req.Model.Trim();
            // Keep this session's A2A/headless path (JLEngineCore.RunTurnAsync ->
            // GetBrainBackend) in sync too, so a model change applies uniformly.
            chat.Engine.Backends.SetBackendModel("openrouter", chat.Runtime.CurrentModel);
            telemetry.LogModelChange(previous, chat.Runtime.CurrentModel);
            return Results.Ok(new { model = chat.Runtime.CurrentModel });
        });

        // --- Operator switcher: which agent card is active for a session ---
        app.MapGet("/api/agents", async (string session) =>
        {
            var chat = await sessions.GetOrCreateAsync(session);
            return Results.Ok(new
            {
                current = chat.Engine.CurrentAgentName,
                agents = chat.Engine.MpfProfiles.Keys.OrderBy(k => k),
            });
        });

        app.MapPost("/api/agent", async (SetAgentRequest req) =>
        {
            var chat = await sessions.GetOrCreateAsync(req.SessionId);
            var previous = chat.Engine.CurrentAgentName;
            // SetAgent silently falls back to the configured default agent for an
            // unrecognized name (faithful port of Julia's own behavior) rather than
            // erroring — this endpoint relies on the GUI only ever sending names it
            // got from GET /api/agents, so that fallback path shouldn't trigger here.
            var ok = chat.Engine.SetAgent(req.Name);
            if (ok) telemetry.LogAgentChange(previous, chat.Engine.CurrentAgentName);
            return ok
                ? Results.Ok(new { agent = chat.Engine.CurrentAgentName })
                : Results.BadRequest(new { error = $"Unknown agent '{req.Name}'", agent = chat.Engine.CurrentAgentName });
        });

        // --- Tool catalog: what SparkByte can do, per session enable/disable ---
        app.MapGet("/api/tools", async (string session) =>
        {
            var chat = await sessions.GetOrCreateAsync(session);
            return Results.Ok(new { tools = chat.Tools.Catalog(ToolSchemas.All()) });
        });

        app.MapPost("/api/tools", async (SetToolRequest req) =>
        {
            var chat = await sessions.GetOrCreateAsync(req.SessionId);
            if (req.Enabled) chat.Tools.DisabledTools.Remove(req.Name);
            else chat.Tools.DisabledTools.Add(req.Name);
            return Results.Ok(new { tools = chat.Tools.Catalog(ToolSchemas.All()) });
        });

        // --- Forged-tool source, for the GUI's edit view ---
        app.MapGet("/api/tools/source", async (string session, string name) =>
        {
            var chat = await sessions.GetOrCreateAsync(session);
            if (!chat.Tools.DynamicSchema.TryGetValue(name, out var schema))
            {
                return Results.NotFound(new { error = $"'{name}' is not a forged tool." });
            }
            var code = await chat.Tools.GetDynamicSourceAsync(name);
            if (code is null) return Results.NotFound(new { error = "No persisted source found for this tool." });
            return Results.Ok(new { name, code, description = schema.Description, parameters = schema.Parameters });
        });

        // --- Craft / edit a tool from the GUI — dispatches through the SAME
        // forge_new_tool tool the LLM calls, so it gets the identical
        // denylist/phantom-capability check, Roslyn compile, and live smoke
        // test. Re-forging an existing name overwrites it (dedup fixed above). ---
        app.MapPost("/api/tools/forge", async (ForgeToolRequest req) =>
        {
            var chat = await sessions.GetOrCreateAsync(req.SessionId);
            var args = new Dictionary<string, object?>
            {
                ["name"] = req.Name,
                ["code"] = req.Code,
                ["description"] = string.IsNullOrWhiteSpace(req.Description) ? $"Crafted from Settings: {req.Name}" : req.Description,
                ["parameters"] = req.Parameters ?? new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>(),
                    ["required"] = new List<object?>(),
                },
            };
            var result = await chat.Tools.DispatchAsync("forge_new_tool", args);
            return result.ContainsKey("error") ? Results.BadRequest(result) : Results.Ok(result);
        });

        // --- Delete a forged tool (built-ins aren't deletable) ---
        app.MapPost("/api/tools/delete", async (DeleteToolRequest req) =>
        {
            var chat = await sessions.GetOrCreateAsync(req.SessionId);
            var removed = await chat.Tools.RemoveDynamicAsync(req.Name);
            return removed
                ? Results.Ok(new { deleted = req.Name })
                : Results.BadRequest(new { error = $"'{req.Name}' isn't a forged tool — built-ins can't be deleted." });
        });

        // --- Run any tool directly from the GUI, bypassing the LLM entirely ---
        app.MapPost("/api/tools/run", async (RunToolRequest req) =>
        {
            var chat = await sessions.GetOrCreateAsync(req.SessionId);
            if (!chat.Tools.Contains(req.Name))
            {
                return Results.NotFound(new { error = $"Unknown tool: '{req.Name}'" });
            }
            var result = await chat.Tools.DispatchAsync(req.Name, req.Args ?? [], chat.Engine.CurrentAgentName);
            return Results.Ok(result);
        });

        // --- File browser: local filesystem, for attaching files to chat ---
        // Global (no session) — it's just browsing disk, not engine state.
        // No extra sandboxing beyond path normalization: run_command/read_file/
        // write_file tools already give the model full filesystem access, so a
        // browse/read HTTP view is not a new trust boundary, just a GUI for one
        // that already exists.
        app.MapGet("/api/fs/list", (string? path) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new
                    {
                        name = d.Name,
                        fullPath = d.Name,
                        isDirectory = true,
                        size = (long?)null,
                    });
                    return Results.Ok(new { path = "", parent = (string?)null, entries = drives });
                }

                var full = Path.GetFullPath(path);
                if (!Directory.Exists(full)) return Results.NotFound(new { error = "Directory not found" });

                var dirInfo = new DirectoryInfo(full);
                var entries = new List<object>();
                foreach (var d in dirInfo.EnumerateDirectories())
                {
                    try { entries.Add(new { name = d.Name, fullPath = d.FullName, isDirectory = true, size = (long?)null }); }
                    catch { /* e.g. access denied on a special system folder — skip it */ }
                }
                foreach (var f in dirInfo.EnumerateFiles())
                {
                    try { entries.Add(new { name = f.Name, fullPath = f.FullName, isDirectory = false, size = (long?)f.Length }); }
                    catch { /* skip unreadable entries rather than fail the whole listing */ }
                }
                return Results.Ok(new { path = full, parent = dirInfo.Parent?.FullName, entries });
            }
            catch (Exception e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

        app.MapGet("/api/fs/read", async (string path) =>
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (!File.Exists(full)) return Results.NotFound(new { error = "File not found" });

                const long maxBytes = 512 * 1024;
                var info = new FileInfo(full);
                if (info.Length > maxBytes)
                {
                    return Results.Ok(new { path = full, truncated = true, binary = false, content = $"[File too large to preview: {info.Length:N0} bytes]" });
                }

                var bytes = await File.ReadAllBytesAsync(full);
                if (bytes.Take(1024).Any(b => b == 0))
                {
                    return Results.Ok(new { path = full, truncated = false, binary = true, content = "[Binary file — preview not available]" });
                }

                return Results.Ok(new { path = full, truncated = false, binary = false, content = Encoding.UTF8.GetString(bytes) });
            }
            catch (Exception e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

        // --- Engine behavior settings, per session ---
        app.MapGet("/api/settings/behavior", async (string session) =>
        {
            var chat = await sessions.GetOrCreateAsync(session);
            return Results.Ok(new
            {
                safetyOn = chat.Runtime.SafetyOn,
                maxToolLoops = chat.Runtime.MaxToolLoops,
                historyLength = chat.Runtime.HistoryLength,
            });
        });

        app.MapPost("/api/settings/behavior", async (SetBehaviorRequest req) =>
        {
            var chat = await sessions.GetOrCreateAsync(req.SessionId);
            if (req.SafetyOn.HasValue) chat.Runtime.SafetyOn = req.SafetyOn;
            if (req.MaxToolLoops.HasValue) chat.Runtime.MaxToolLoops = Math.Max(1, req.MaxToolLoops.Value);
            if (req.HistoryLength.HasValue) chat.Runtime.HistoryLength = Math.Max(1, req.HistoryLength.Value);
            return Results.Ok(new
            {
                safetyOn = chat.Runtime.SafetyOn,
                maxToolLoops = chat.Runtime.MaxToolLoops,
                historyLength = chat.Runtime.HistoryLength,
            });
        });

        // --- Credentials: known integration env vars, never echoed back ---
        app.MapGet("/api/settings/credentials", () => Results.Ok(new
        {
            credentials = KnownCredentials.Select(name => new
            {
                name,
                set = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)) ||
                    !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)),
            }),
        }));

        app.MapPost("/api/settings/credentials", (SetCredentialRequest req) =>
        {
            if (!KnownCredentials.Contains(req.Name)) return Results.BadRequest(new { error = $"Unknown credential '{req.Name}'." });
            if (string.IsNullOrWhiteSpace(req.Value)) return Results.BadRequest(new { error = "value is required" });

            // Apply to the running process immediately (takes effect on the very
            // next tool/backend call that reads it) and persist to the user's
            // environment so it survives a restart — same approach used for
            // OPENROUTER_API_KEY, just exposed through the GUI instead of PowerShell.
            Environment.SetEnvironmentVariable(req.Name, req.Value, EnvironmentVariableTarget.Process);
            try { Environment.SetEnvironmentVariable(req.Name, req.Value, EnvironmentVariableTarget.User); }
            catch { /* best-effort persistence; the process-scope value above still works this session */ }

            return Results.Ok(new { name = req.Name, set = true });
        });
    }

    private static readonly string[] KnownCredentials =
    [
        "OPENAI_API_KEY",
        "OPENROUTER_API_KEY",
        "GITHUB_TOKEN",
        "TWILIO_ACCOUNT_SID",
        "TWILIO_AUTH_TOKEN",
        "TWILIO_FROM_NUMBER",
        "DISCORD_WEBHOOK_URL",
        "TELEGRAM_BOT_TOKEN",
    ];

    private static string NormalizeChatGptSession(string? session)
    {
        if (string.IsNullOrWhiteSpace(session)) return "chatgpt-private";
        if (session.Length > 64 || session.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            return "chatgpt-private";
        return session;
    }

    private static void AppendHistory(ChatSession chat, string userText, string reply)
    {
        chat.History.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = userText });
        chat.History.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = reply });
        var maxEntries = Math.Max(1, chat.Runtime.HistoryLength) * 2;
        while (chat.History.Count > maxEntries) chat.History.RemoveAt(0);
    }
}
