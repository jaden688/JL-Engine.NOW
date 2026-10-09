using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JLEngine.Bridges.A2a;
using JLEngine.Core.Engine;
using JLEngine.Core.Types;
using JLEngine.Runtime.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JLEngine.Bridges.Tests;

public class A2aServerTests : IAsyncLifetime
{
    private SqliteConnection _db = null!;
    private A2aServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private Task _serverTask = null!;
    private HttpClient _http = null!;
    private int _port;

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("A2A_API_KEY", null);
        Environment.SetEnvironmentVariable("A2A_ADMIN_KEY", null);
        Environment.SetEnvironmentVariable("A2A_MAX_REQUESTS_PER_MINUTE", null);

        _db = new SqliteConnection("Data Source=:memory:");
        _db.Open();
        A2aTaskStore.EnsureSchema(_db);
        A2aBilling.EnsureSchema(_db);

        var engine = new JLEngineCore(new EngineConfig { RootDir = Path.Combine(Path.GetTempPath(), $"a2a-test-{Guid.NewGuid()}") });
        engine.Backends.SetBrainBackendId("noop-stub"); // avoid needing a real API key in tests

        var toolRegistry = new ToolRegistry(Path.Combine(Path.GetTempPath(), $"a2a-tools-{Guid.NewGuid()}"));
        toolRegistry.Register(new EchoTestTool());

        var taskStore = new A2aTaskStore(_db);
        var billing = new A2aBilling(_db);
        _port = GetFreePort();
        _server = new A2aServer(engine, toolRegistry, ToolSchemas.BuiltinSchemas, taskStore, billing, $"http://127.0.0.1:{_port}");

        _cts = new CancellationTokenSource();
        _serverTask = _server.RunAsync("127.0.0.1", _port, _cts.Token);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        return Task.Delay(300); // let HttpListener spin up
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { await _serverTask; } catch { /* expected on cancellation */ }
        _http.Dispose();
        _db.Dispose();
    }

    private sealed class EchoTestTool : ITool
    {
        public string Name => "echo_test";
        public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args) =>
            Task.FromResult(new Dictionary<string, object?> { ["result"] = args.GetValueOrDefault("text", "") });
    }

    [Fact]
    public async Task AgentCard_IsDiscoverableAtWellKnownPath()
    {
        var response = await _http.GetAsync("/.well-known/agent.json");
        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("JL Engine", doc.RootElement.GetProperty("name").GetString());
        Assert.True(doc.RootElement.GetProperty("skills").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _http.GetAsync("/health");
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task MessageSend_PlainTextChat_RoutesThroughEngineNoopBackend()
    {
        var rpcRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "message/send",
            @params = new { message = new { parts = new[] { new { text = "hello from a2a test" } } } },
        };
        var response = await _http.PostAsync("/", new StringContent(JsonSerializer.Serialize(rpcRequest), Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("result", out var result));
        var task = result.GetProperty("task");
        Assert.Equal("TASK_STATE_COMPLETED", task.GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task MessageSend_JsonToolPayload_DispatchesToolDirectly()
    {
        var rpcRequest = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "message/send",
            @params = new { message = new { parts = new[] { new { text = """{"tool":"echo_test","args":{"text":"direct-dispatch"}}""" } } } },
        };
        var response = await _http.PostAsync("/", new StringContent(JsonSerializer.Serialize(rpcRequest), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var task = doc.RootElement.GetProperty("result").GetProperty("task");
        Assert.Equal("TASK_STATE_COMPLETED", task.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("echo_test", task.GetProperty("metadata").GetProperty("tool").GetString());
    }

    [Fact]
    public async Task TasksGet_AfterMessageSend_ReturnsTheCompletedTask()
    {
        var taskId = Guid.NewGuid().ToString();
        var sendRequest = new
        {
            jsonrpc = "2.0", id = 3, method = "message/send",
            @params = new { message = new { taskId, parts = new[] { new { text = "task lookup test" } } } },
        };
        await _http.PostAsync("/", new StringContent(JsonSerializer.Serialize(sendRequest), Encoding.UTF8, "application/json"));

        var getRequest = new { jsonrpc = "2.0", id = 4, method = "tasks/get", @params = new { id = taskId } };
        var response = await _http.PostAsync("/", new StringContent(JsonSerializer.Serialize(getRequest), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(taskId, doc.RootElement.GetProperty("result").GetProperty("id").GetString());
    }
}
