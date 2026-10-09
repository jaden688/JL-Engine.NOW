using JLEngine.Bridges;

// Standalone bridge process (matches Julia's telegram_listener.jl — a
// separate script you run alongside the engine, not something the engine
// itself boots). Requires TELEGRAM_BOT_TOKEN in the environment or a .env
// file in the current directory; no-ops with a clear message otherwise.

var projectRoot = Directory.GetCurrentDirectory();
var token = TelegramListener.ResolveToken(projectRoot);
var wsUrl = Environment.GetEnvironmentVariable("JLENGINE_WS_URL") ?? "ws://127.0.0.1:8081/ws";

using var http = new HttpClient();
var listener = new TelegramListener(token, wsUrl, http, msg => Console.WriteLine(msg));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await listener.RunAsync(cts.Token);
