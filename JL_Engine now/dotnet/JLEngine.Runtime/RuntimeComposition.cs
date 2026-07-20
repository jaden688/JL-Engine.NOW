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
}

/// <summary>Lazily creates and caches one ChatSession per client-generated
/// session ID. Uses Task-caching GetOrAdd so concurrent requests for a
/// brand-new session id await the same in-flight build rather than racing
/// to construct two independent engines for it.</summary>
public sealed class SessionRegistry(SharedComponents shared)
{
    private readonly ConcurrentDictionary<string, Task<ChatSession>> _sessions = new();

    public Task<ChatSession> GetOrCreateAsync(string sessionId) =>
        _sessions.GetOrAdd(sessionId, id => RuntimeComposition.CreateSessionAsync(shared, id));

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
        foreach (var stub in NotPortedTool.All()) toolRegistry.Register(stub);

        // Port of init_tools's tools-table sync: upsert one row per declared
        // schema, preserving any existing call_count (builtins, then dynamic).
        // Idempotent, so re-running it for every new session is harmless.
        foreach (var schema in ToolSchemas.BuiltinSchemas.Values)
        {
            db.UpsertToolSchema(schema.Name, "builtin", schema.Description, JsonSerializer.Serialize(schema.Parameters), false);
        }
        foreach (var schema in ToolSchemas.StubSchemas.Values)
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

        var agentRuntime = new AgentRuntime(engine, toolRegistry, telemetry, http, ToolSchemas.All(), db);
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
                emotion = apertureState.Emotion,
                investmentGear = InvestmentSystem.InvestmentGear(chat.Engine.InvestmentSystem.Level),
                stability = chat.Engine.StabilityScore,
            });
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

                var reply = await chat.Runtime.ProcessMessageAsync(userText, chat.History, chat.Runtime.CurrentModel);
                chat.History.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = userText });
                chat.History.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = reply });

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
    }
}
