using System.Net;
using System.Text;
using System.Text.Json;
using JLEngine.Core.Config;
using JLEngine.Core.Engine;
using JLEngine.Runtime.Tools;

namespace JLEngine.Bridges.A2a;

/// <summary>
/// Port of a2a_server.jl — the Google A2A (agent-to-agent) HTTP endpoint,
/// separate from the BYTE WebSocket port (matches Julia: BYTE on 8081, A2A
/// on 8082). Built on the built-in HttpListener rather than ASP.NET Core, so
/// this class library doesn't need a Kestrel/hosting dependency of its own.
///
/// Explicit scope decision (this port covers a working core, not the full
/// surface of the 1533-line Julia source):
/// PORTED: agent-card discovery (GET /.well-known/agent.json), JSON-RPC
///   message/send and tasks/get, health check, bearer-token auth + rate
///   limiting + usage-ledger recording (via A2aBilling).
/// NOT PORTED (explicitly out of scope): SSE streaming responses
///   (message/stream), push-notification webhook subscriptions, the
///   extended-agent-card RPC, the Stripe checkout/webhook/subscription
///   lifecycle, and the HTML welcome page. These either require live
///   third-party infrastructure (Stripe) or are secondary transport
///   variants (SSE) layered on top of the same core logic ported here.
///
/// Faithful to Julia's actual `_run_task` dispatch: plain-text input runs
/// through JLEngineCore's headless `RunTurnAsync` pipeline (NOT the BYTE
/// tool-calling loop) exactly like `Main.JLEngine.run_turn!` does; a JSON
/// payload shaped like {"tool":"...","args":{...}} dispatches that one tool
/// directly via the tool registry, matching `BYTE.dispatch(tool, args)`.
/// </summary>
public sealed class A2aServer(
    JLEngineCore engine,
    ToolRegistry tools,
    IReadOnlyDictionary<string, ToolSchemaEntry> toolSchemas,
    A2aTaskStore taskStore,
    A2aBilling billing,
    string publicUrl)
{
    private HttpListener? _listener;

    public async Task RunAsync(string host, int port, CancellationToken ct)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{host}:{port}/");
        _listener.Start();

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            _ = HandleAsync(context);
        }

        _listener.Stop();
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var response = await RouteAsync(context.Request);
            context.Response.StatusCode = response.Status;
            context.Response.ContentType = "application/json";
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            var bytes = Encoding.UTF8.GetBytes(response.Body);
            await context.Response.OutputStream.WriteAsync(bytes);
        }
        catch
        {
            context.Response.StatusCode = 500;
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task<(int Status, string Body)> RouteAsync(HttpListenerRequest req)
    {
        var path = req.Url?.AbsolutePath ?? "/";
        var method = req.HttpMethod;

        if (path is "/.well-known/agent.json" or "/.well-known/agent-card.json" && method == "GET")
        {
            return (200, JsonSerializer.Serialize(A2aAgentCard.Build(publicUrl, toolSchemas)));
        }

        if (path == "/health" && method == "GET")
        {
            return (200, JsonSerializer.Serialize(new { status = "ok", agent = A2aAgentCard.AgentName }));
        }

        if (path.StartsWith("/tasks/") && method == "GET")
        {
            var taskId = path["/tasks/".Length..];
            var task = taskStore.GetTask(taskId);
            return task is null ? (404, JsonSerializer.Serialize(new { error = $"Task not found: {taskId}" })) : (200, JsonSerializer.Serialize(task));
        }

        if (path == "/" && method == "POST")
        {
            using var reader = new StreamReader(req.InputStream);
            var body = await reader.ReadToEndAsync();
            return await HandleJsonRpcAsync(req, body);
        }

        return (404, JsonSerializer.Serialize(new { error = "Not found" }));
    }

    private async Task<(int Status, string Body)> HandleJsonRpcAsync(HttpListenerRequest req, string bodyText)
    {
        Dictionary<string, object?> body;
        object? rpcId;
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            body = JLEngine.Core.Config.JsonLoader.Materialize(doc.RootElement) as Dictionary<string, object?> ?? [];
            rpcId = body.TryGetValue("id", out var idVal) ? idVal : null;
        }
        catch
        {
            return (200, JsonSerializer.Serialize(RpcError(null, -32700, "Parse error")));
        }

        var meth = body.TryGetValue("method", out var m) ? m?.ToString() ?? "" : "";
        var apiKey = ExtractBearerToken(req) ?? "";
        var authErr = billing.CheckAuth(apiKey);
        if (authErr is not null)
        {
            return ((int)authErr.Value.Code, JsonSerializer.Serialize(new { error = authErr.Value.Message }));
        }

        var paramsDict = body.TryGetValue("params", out var p) ? p as Dictionary<string, object?> ?? [] : [];

        return meth switch
        {
            "message/send" or "SendMessage" or "tasks/send" => await HandleMessageSendAsync(rpcId, meth, paramsDict, apiKey),
            "tasks/get" or "GetTask" => HandleTasksGet(rpcId, paramsDict),
            _ => (200, JsonSerializer.Serialize(RpcError(rpcId, -32601, $"Method not found: {meth}"))),
        };
    }

    private async Task<(int, string)> HandleMessageSendAsync(object? rpcId, string meth, Dictionary<string, object?> paramsDict, string apiKey)
    {
        var entitlementErr = billing.TaskEntitlementBlock(apiKey);
        if (entitlementErr is not null)
        {
            return (entitlementErr.Value.Code, JsonSerializer.Serialize(new { error = entitlementErr.Value.Message }));
        }

        var message = paramsDict.GetOrDict("message") ?? [];
        var taskId = (message.GetOr("taskId") ?? paramsDict.GetOr("taskId") ?? paramsDict.GetOr("id") ?? Guid.NewGuid().ToString()).ToString()!;
        var existingState = taskStore.GetTaskState(taskId);
        if (existingState is not null && A2aTaskStore.IsTerminalState(existingState))
        {
            return (200, JsonSerializer.Serialize(RpcError(rpcId, -32002, $"Task already completed and cannot be restarted: {taskId}")));
        }

        var contextId = (message.GetOr("contextId") ?? paramsDict.GetOr("contextId") ?? Guid.NewGuid().ToString()).ToString()!;
        var text = MessageText(message, paramsDict);
        var (tool, toolArgs) = ExtractToolAndArgs(text);

        taskStore.LogTask(taskId, apiKey, text, tool, toolArgs);

        var started = DateTime.UtcNow;
        Dictionary<string, object?> result;
        try
        {
            if (tool == "chat")
            {
                // Port of `_run_task`'s chat branch: routes through the engine's
                // HEADLESS pipeline (RunTurnAsync), not the BYTE tool-calling loop —
                // matching Julia's `Main.JLEngine.run_turn!` call exactly.
                var turnResult = await engine.RunTurnAsync(text);
                result = new Dictionary<string, object?> { ["text"] = turnResult["reply"], ["source"] = "engine" };
            }
            else
            {
                var toolResult = await tools.DispatchAsync(tool, toolArgs);
                result = new Dictionary<string, object?>(toolResult);
            }
        }
        catch (Exception e)
        {
            result = new Dictionary<string, object?> { ["error"] = e.Message };
        }
        var elapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds;

        var statusState = result.ContainsKey("error") ? "TASK_STATE_FAILED" : "TASK_STATE_COMPLETED";
        var agentText = ResultText(result);

        var task = new Dictionary<string, object?>
        {
            ["id"] = taskId,
            ["contextId"] = contextId,
            ["status"] = new Dictionary<string, object?> { ["state"] = statusState, ["timestamp"] = DateTime.UtcNow.ToString("O") },
            ["history"] = new List<object?>
            {
                new Dictionary<string, object?> { ["role"] = "ROLE_USER", ["parts"] = MessageParts(text), ["taskId"] = taskId, ["contextId"] = contextId },
                new Dictionary<string, object?> { ["role"] = "ROLE_AGENT", ["parts"] = MessageParts(agentText), ["taskId"] = taskId, ["contextId"] = contextId },
            },
            ["artifacts"] = statusState == "TASK_STATE_COMPLETED"
                ? new List<object?> { new Dictionary<string, object?> { ["name"] = tool, ["parts"] = MessageParts(agentText) } }
                : new List<object?>(),
            ["metadata"] = new Dictionary<string, object?> { ["tool"] = tool, ["elapsed_ms"] = elapsedMs, ["request_chars"] = text.Length },
        };

        if (statusState == "TASK_STATE_COMPLETED") taskStore.CompleteTask(taskId, task, elapsedMs);
        else taskStore.FailTask(taskId, agentText, elapsedMs);

        billing.RecordUsage(apiKey, taskId, meth, text.Length, agentText.Length, tool == "chat" ? 0 : 1, statusState);

        var rpcResult = meth == "tasks/send" ? (object)task : new Dictionary<string, object?> { ["task"] = task };
        return (200, JsonSerializer.Serialize(RpcResult(rpcId, rpcResult)));
    }

    private (int, string) HandleTasksGet(object? rpcId, Dictionary<string, object?> paramsDict)
    {
        var taskId = paramsDict.GetOr("id")?.ToString() ?? "";
        if (string.IsNullOrEmpty(taskId))
        {
            return (200, JsonSerializer.Serialize(RpcError(rpcId, -32602, "Task id is required")));
        }
        var task = taskStore.GetTask(taskId);
        return task is null
            ? (200, JsonSerializer.Serialize(RpcError(rpcId, -32001, $"Task not found: {taskId}")))
            : (200, JsonSerializer.Serialize(RpcResult(rpcId, task)));
    }

    /// <summary>Port of `_extract_tool_and_args`: JSON payload with a "tool" key
    /// dispatches that tool directly; anything else is plain-text chat.</summary>
    private static (string Tool, Dictionary<string, object?> Args) ExtractToolAndArgs(string messageText)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageText);
            if (JLEngine.Core.Config.JsonLoader.Materialize(doc.RootElement) is Dictionary<string, object?> parsed && parsed.TryGetValue("tool", out var toolObj))
            {
                var args = parsed.GetOrDict("args") ?? [];
                return (toolObj?.ToString() ?? "chat", args);
            }
        }
        catch { /* not JSON — fall through to chat */ }

        return ("chat", new Dictionary<string, object?> { ["text"] = messageText });
    }

    private static string MessageText(Dictionary<string, object?> message, Dictionary<string, object?> paramsDict)
    {
        if (message.GetOrList("parts") is { Count: > 0 } parts && parts[0] is Dictionary<string, object?> firstPart)
        {
            return firstPart.GetOr("text")?.ToString() ?? "";
        }
        return paramsDict.GetOr("input")?.ToString() ?? paramsDict.GetOr("text")?.ToString() ?? "";
    }

    private static string ResultText(Dictionary<string, object?> result)
    {
        if (result.TryGetValue("error", out var err)) return err?.ToString() ?? "error";
        if (result.TryGetValue("text", out var text)) return text?.ToString() ?? "";
        if (result.TryGetValue("result", out var res)) return res?.ToString() ?? "";
        return JsonSerializer.Serialize(result);
    }

    private static List<object?> MessageParts(string text) => [new Dictionary<string, object?> { ["kind"] = "text", ["text"] = text }];

    private static string? ExtractBearerToken(HttpListenerRequest req)
    {
        var header = req.Headers["Authorization"];
        if (string.IsNullOrEmpty(header)) return null;
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : header.Trim();
    }

    private static Dictionary<string, object?> RpcResult(object? id, object? result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static Dictionary<string, object?> RpcError(object? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message } };
}
