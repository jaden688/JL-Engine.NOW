using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JLEngine.Runtime.Tools;

/// <summary>Port of Tools.jl's browse_url — a plain HTTP GET fetch (the
/// Julia version also has a Playwright-backed browser_context path via
/// playwright_interact for JS-rendered pages; this covers the plain-fetch
/// case, which is what most browse_url calls in the telemetry actually did).</summary>
public sealed partial class BrowseUrlTool(HttpClient http) : ITool
{
    public string Name => "browse_url";

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var url = ToolArgs.GetArg(args, "url", "link", "address");
        if (string.IsNullOrEmpty(url))
        {
            return new Dictionary<string, object?> { ["error"] = "Missing required argument: 'url'" };
        }

        try
        {
            var response = await http.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return new Dictionary<string, object?> { ["error"] = $"HTTP {(int)response.StatusCode} fetching {url}" };
            }
            var text = StripTags().Replace(body, " ");
            return new Dictionary<string, object?> { ["result"] = text.Length > 5000 ? text[..5000] : text, ["url"] = url };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["error"] = $"Fetch failed: {e.Message}" };
        }
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex StripTags();
}

/// <summary>Port of Tools.jl's GitHub-pillage tool — fetches repo file trees
/// and raw file contents via the GitHub REST API, converting github.com
/// blob URLs to the raw content endpoint.</summary>
public sealed partial class GithubPillageTool(HttpClient http) : ITool
{
    public string Name => "github_pillage";

    [GeneratedRegex(@"https?://github\.com/([^/]+)/([^/]+)/blob/(.+)")]
    private static partial Regex BlobUrlPattern();

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var url = ToolArgs.GetArg(args, "url", "repo", "path");
        if (string.IsNullOrEmpty(url))
        {
            return new Dictionary<string, object?> { ["error"] = "Missing required argument: 'url'" };
        }

        var match = BlobUrlPattern().Match(url);
        var rawUrl = match.Success
            ? $"https://raw.githubusercontent.com/{match.Groups[1].Value}/{match.Groups[2].Value}/{match.Groups[3].Value}"
            : url;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, rawUrl);
            request.Headers.UserAgent.ParseAdd("JLEngine");
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return response.IsSuccessStatusCode
                ? new Dictionary<string, object?> { ["result"] = body, ["source"] = rawUrl }
                : new Dictionary<string, object?> { ["error"] = $"HTTP {(int)response.StatusCode} fetching {rawUrl}" };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["error"] = $"Fetch failed: {e.Message}" };
        }
    }
}

/// <summary>Port of Tools.jl's discord_webhook.</summary>
public sealed class DiscordWebhookTool(HttpClient http) : ITool
{
    public string Name => "discord_webhook";

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        var explicitUrl = ToolArgs.GetArg(args, "webhook_url");
        var url = string.IsNullOrEmpty(explicitUrl) ? webhookUrl : explicitUrl;
        var message = ToolArgs.GetArg(args, "message", "content", "text");

        if (string.IsNullOrEmpty(url))
        {
            return new Dictionary<string, object?> { ["error"] = "Discord webhook is not configured (set DISCORD_WEBHOOK_URL or pass webhook_url)." };
        }
        if (string.IsNullOrEmpty(message))
        {
            return new Dictionary<string, object?> { ["error"] = "Missing required argument: 'message'" };
        }

        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["content"] = message });
            var response = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
            return response.IsSuccessStatusCode
                ? new Dictionary<string, object?> { ["result"] = "Message posted to Discord." }
                : new Dictionary<string, object?> { ["error"] = $"Discord webhook returned HTTP {(int)response.StatusCode}." };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["error"] = $"Webhook post failed: {e.Message}" };
        }
    }
}
