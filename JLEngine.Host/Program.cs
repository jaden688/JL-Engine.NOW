using JLEngine.Bridges;
using JLEngine.Bridges.A2a;
using JLEngine.Runtime;
using JLEngine.Runtime.Tools;
using Microsoft.Extensions.FileProviders;

// Composed process: WebSocket chat server (matches Julia's BYTE on 8081) +
// A2A protocol server on its own port (matches Julia's A2A on 8082) +
// the Autopilot background loop — all sharing one JLEngineCore/database,
// mirroring how src/App.jl boots BYTE + a2a_server.jl together in one process.
var builder = WebApplication.CreateBuilder(args);
// Default bind; an explicit --urls arg or ASPNETCORE_URLS env var overrides
// this (those configuration sources are layered on top of UseUrls).
builder.WebHost.UseUrls("http://127.0.0.1:8081");
var app = builder.Build();
// Serve the chat GUI from the exe's own folder rather than the process's
// working directory — the engine is launched with cwd = the data/ folder
// (so its config/agent-card lookups resolve), which is not where wwwroot
// lives, so env.WebRootFileProvider (rooted at ContentRootPath) is bypassed
// in favor of an explicit provider rooted at the binary's directory.
var staticFileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = staticFileProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = staticFileProvider });

var projectRoot = Directory.GetCurrentDirectory();
var shared = await RuntimeComposition.BuildSharedAsync(projectRoot);
var sessions = new SessionRegistry(shared);
app.MapChatEndpoints(shared, sessions);

// A2A and Autopilot aren't tab-based — they get one fixed, long-lived session
// (matching the real Julia app's single "current agent" instance), separate
// from whatever independent sessions the GUI's chat tabs create for themselves.
var defaultSession = await sessions.GetOrCreateAsync("default");

using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);

// --- A2A protocol server, on its own port ---
var a2aPort = int.TryParse(Environment.GetEnvironmentVariable("A2A_PORT"), out var p) ? p : 8082;
var a2aHost = Environment.GetEnvironmentVariable("A2A_HOST") ?? "127.0.0.1";
var a2aPublicUrl = Environment.GetEnvironmentVariable("A2A_PUBLIC_URL") ?? $"http://localhost:{a2aPort}";

A2aTaskStore.EnsureSchema(shared.Db.Connection);
A2aBilling.EnsureSchema(shared.Db.Connection);
var a2aTaskStore = new A2aTaskStore(shared.Db.Connection);
var a2aBilling = new A2aBilling(shared.Db.Connection);
var a2aServer = new A2aServer(defaultSession.Engine, defaultSession.Tools, ToolSchemas.All(), a2aTaskStore, a2aBilling, a2aPublicUrl);
var a2aTask = a2aServer.RunAsync(a2aHost, a2aPort, lifetimeCts.Token);
Console.WriteLine($"A2A Agent Card -> {a2aPublicUrl}/.well-known/agent.json");

// --- Autopilot background loop (no-op unless SPARKBYTE_AUTOPILOT_SECONDS is set) ---
var autopilotOptions = AutopilotOptions.FromEnvironment();
var autopilot = new AutopilotService(defaultSession.Engine, shared.Db, autopilotOptions,
    msg => shared.Telemetry.LogEvent(msg.GetValueOrDefault("type")?.ToString() ?? "autopilot_broadcast", msg));
var autopilotTask = autopilot.RunAsync(lifetimeCts.Token);
Console.WriteLine(autopilotOptions.IntervalSeconds < 0
    ? "Autopilot: disabled (set SPARKBYTE_AUTOPILOT_SECONDS to enable)"
    : $"Autopilot: ticking every {autopilotOptions.IntervalSeconds}s");

app.Lifetime.ApplicationStopping.Register(() => lifetimeCts.Cancel());

try
{
    await app.RunAsync();
}
finally
{
    await Task.WhenAll(
        a2aTask.ContinueWith(_ => { }),
        autopilotTask.ContinueWith(_ => { }));
}
