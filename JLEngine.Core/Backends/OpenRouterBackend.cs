using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JLEngine.Core.Config;

namespace JLEngine.Core.Backends;

/// <summary>
/// Port of Backends.jl's OpenRouterBackend. Synchronous/blocking in Julia
/// (a single HTTP.post call); ported as async Task per .NET convention,
/// which changes HOW concurrency is expressed, not turn semantics (a turn
/// is still logically serial). Faithful port: errors become "[ERROR: ...]"
/// sentinel strings in the reply, not exceptions — see IBackend's docs.
/// </summary>
public sealed class OpenRouterBackend(Dictionary<string, object?> config, HttpClient? httpClient = null) : IBackend
{
    public const string DefaultEndpoint = "https://openrouter.ai/api/v1/chat/completions";
    public const string DefaultModel = "deepseek/deepseek-v4-flash";

    public Dictionary<string, object?> Config { get; } = config;
    private readonly HttpClient _http = httpClient ?? SharedHttpClient.Instance;

    public async Task<(string Reply, Dictionary<string, object?> Meta)> GenerateAsync(
        List<Dictionary<string, object?>> messages,
        Dictionary<string, object?>? options = null,
        int? timeoutSeconds = null)
    {
        options ??= [];
        var endpoint = Config.GetOrString("endpoint", DefaultEndpoint);
        var model = Config.GetOr("model") as string ?? Config.GetOr("model_name") as string ?? DefaultModel;

        var apiKey = Config.GetOr("api_key") as string;
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
        }
        if (string.IsNullOrEmpty(apiKey))
        {
            return ("[ERROR: OpenRouter API key is not set.]", new Dictionary<string, object?> { ["error"] = "api_key_missing" });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => (object?)m).ToList(),
        };
        if (options.Count > 0)
        {
            if (options.TryGetValue("temperature", out var temp)) payload["temperature"] = temp;
            if (options.TryGetValue("top_p", out var topP)) payload["top_p"] = topP;
            if (options.TryGetValue("max_tokens", out var maxTokens)) payload["max_tokens"] = maxTokens;
        }

        var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? (int)Config.GetOrDouble("timeout", 120));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("HTTP-Referer", "http://localhost:8081");
            request.Headers.Add("X-Title", "JL Engine");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var json = JsonValueConverter.ToJsonNode(payload)!.ToJsonString();
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(timeout);
            using var response = await _http.SendAsync(request, cts.Token);
            var bodyText = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return HandleErrorResponse(bodyText, (int)response.StatusCode);
            }

            return ParseSuccessResponse(bodyText, model);
        }
        catch (OperationCanceledException)
        {
            return ("[ERROR: Could not connect to OpenRouter.]", new Dictionary<string, object?> { ["error"] = "timeout" });
        }
        catch (Exception exc)
        {
            return ("[ERROR: Could not connect to OpenRouter.]", new Dictionary<string, object?> { ["error"] = exc.Message });
        }
    }

    private static (string, Dictionary<string, object?>) ParseSuccessResponse(string bodyText, string model)
    {
        Dictionary<string, object?> data;
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            data = JsonLoader.Materialize(doc.RootElement) as Dictionary<string, object?> ?? [];
        }
        catch
        {
            return ("[ERROR: Unexpected response format from OpenRouter.]", new Dictionary<string, object?> { ["error"] = "bad_format", ["raw"] = bodyText });
        }

        if (data.TryGetValue("error", out var errorObj) && errorObj is not null)
        {
            var errMsg = errorObj is Dictionary<string, object?> errDict ? errDict.GetOrString("message", errorObj.ToString() ?? "") : errorObj.ToString() ?? "";
            return ($"[ERROR: OpenRouter returned an error. Details: {errMsg}]", new Dictionary<string, object?> { ["error"] = errMsg });
        }

        if (data.GetOrList("choices") is { Count: > 0 } choices && choices[0] is Dictionary<string, object?> choice)
        {
            var message = choice.GetOrDict("message");
            var content = message?.GetOr("content") as string ?? "";
            var finishReason = choice.GetOr("finish_reason") as string ?? "unknown";

            if (string.IsNullOrWhiteSpace(content))
            {
                return (
                    $"[ERROR: Empty response from OpenRouter model {model} (finish_reason={finishReason}).]",
                    new Dictionary<string, object?> { ["error"] = "empty_response", ["model"] = model, ["backend"] = "openrouter", ["finish_reason"] = finishReason });
            }

            return (content, new Dictionary<string, object?> { ["model"] = model, ["backend"] = "openrouter", ["finish_reason"] = finishReason });
        }

        return ("[ERROR: Unexpected response format from OpenRouter.]", new Dictionary<string, object?> { ["error"] = "bad_format", ["raw"] = data });
    }

    private static (string, Dictionary<string, object?>) HandleErrorResponse(string bodyText, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var data = JsonLoader.Materialize(doc.RootElement) as Dictionary<string, object?> ?? [];
            if (data.TryGetValue("error", out var errorObj) && errorObj is not null)
            {
                var errMsg = errorObj is Dictionary<string, object?> errDict ? errDict.GetOrString("message", errorObj.ToString() ?? "") : errorObj.ToString() ?? "";
                return ($"[ERROR: OpenRouter returned an error. Details: {errMsg}]", new Dictionary<string, object?> { ["error"] = errMsg, ["status"] = status });
            }
        }
        catch
        {
            // fall through to raw-body error below, matching Julia's swallowed inner catch
        }

        return ($"[ERROR: OpenRouter returned HTTP {status}.]", new Dictionary<string, object?> { ["error"] = bodyText, ["status"] = status });
    }
}

internal static class SharedHttpClient
{
    public static readonly HttpClient Instance = new();
}
