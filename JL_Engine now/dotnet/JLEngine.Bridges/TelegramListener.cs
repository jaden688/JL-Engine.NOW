using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace JLEngine.Bridges;

/// <summary>
/// Port of telegram_listener.jl — a standalone bridge process that long-polls
/// the Telegram Bot API and relays messages to/from the engine's WebSocket.
///
/// Protocol adaptation: Julia's listener accumulates streamed "spark" chunk
/// messages until an "engine_state" message signals the turn is done — that
/// matches BYTE.jl's streaming WS protocol. JLEngine.Runtime's WS endpoint
/// (Phase 2) is not chunk-streaming — it sends one {"type":"reply","text":...}
/// message per turn — so this bridge waits for that single reply instead.
/// The bridging *behavior* (relay Telegram → engine → Telegram) is faithful;
/// the wire-level wait logic is adapted to the actual WS contract this port
/// built, not Julia's.
/// </summary>
public sealed class TelegramListener(string botToken, string wsUrl, HttpClient http, Action<string> log)
{
    private const int ReplyTimeoutSeconds = 180;

    public static string ResolveToken(string projectRoot)
    {
        var envToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken)) return envToken.Trim().Trim('"', '\'');

        var envFile = Path.Combine(projectRoot, ".env");
        if (!File.Exists(envFile)) return "";
        foreach (var line in File.ReadLines(envFile))
        {
            if (!line.TrimStart().StartsWith("TELEGRAM_BOT_TOKEN=")) continue;
            var parts = line.Split('=', 2);
            return parts.Length == 2 ? parts[1].Trim().Trim('"', '\'') : "";
        }
        return "";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(botToken))
        {
            log("ERROR: TELEGRAM_BOT_TOKEN not found in environment or .env");
            return;
        }

        long offset = 0;
        log("Starting Telegram listener.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{botToken}/getUpdates?timeout=10&offset={offset}";
                using var response = await http.GetAsync(url, ct);
                var bodyText = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(bodyText);
                var root = doc.RootElement;

                if (root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean() &&
                    root.TryGetProperty("result", out var results) && results.GetArrayLength() > 0)
                {
                    foreach (var update in results.EnumerateArray())
                    {
                        offset = update.GetProperty("update_id").GetInt64() + 1;
                        if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("text", out var textProp)) continue;

                        var msg = textProp.GetString() ?? "";
                        var sender = message.GetProperty("from").GetProperty("first_name").GetString() ?? "unknown";
                        var chatId = message.GetProperty("chat").GetProperty("id").GetInt64();

                        log($"Received from {sender}: {msg}");

                        var prompt = $"[TELEGRAM MESSAGE from {sender}]\n{msg}\n\n" +
                            "(System: Reply directly with the exact Telegram response text. Do not mention tools, WebSockets, or internal routing.)";
                        var reply = await RequestEngineReplyAsync(prompt, ct);
                        var cleaned = reply.Trim();
                        var isNoise = string.IsNullOrEmpty(cleaned) || cleaned.StartsWith('⊣') ||
                            cleaned.Contains("*Aborted.*") || cleaned.Contains("Stop requested") || cleaned.Contains("Nothing is generating");

                        if (isNoise)
                        {
                            log("Skipped internal engine notice (not forwarded to Telegram).");
                        }
                        else if (await SendTelegramMessageAsync(chatId, reply, ct))
                        {
                            log("Sent reply to Telegram.");
                        }
                        else
                        {
                            log("ERROR: Engine produced no Telegram reply before timeout.");
                        }
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (!e.Message.Contains("Timeout"))
                {
                    log($"Error polling Telegram: {e.Message}");
                    await Task.Delay(2000, ct);
                }
            }

            await Task.Delay(500, ct);
        }
    }

    private async Task<bool> SendTelegramMessageAsync(long chatId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        var payload = JsonSerializer.Serialize(new { chat_id = chatId.ToString(), text });
        using var response = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        if (!response.IsSuccessStatusCode) return false;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
    }

    private async Task<string> RequestEngineReplyAsync(string prompt, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ReplyTimeoutSeconds));

        try
        {
            await ws.ConnectAsync(new Uri(wsUrl), timeoutCts.Token);
            var payload = JsonSerializer.Serialize(new { text = prompt });
            await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, timeoutCts.Token);

            var buffer = new byte[1024 * 64];
            var received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeoutCts.Token);
            var raw = Encoding.UTF8.GetString(buffer, 0, received.Count);
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { /* ignore */ }
            }
        }
    }
}
