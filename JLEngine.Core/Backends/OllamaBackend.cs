using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JLEngine.Core.Config;

namespace JLEngine.Core.Backends;

/// <summary>
/// Local-model backend for Ollama. Keeps the same contract as the other backends:
/// returns a sentinel error string embedded in the reply rather than throwing.
/// </summary>
public sealed class OllamaBackend(Dictionary<string, object?> config, HttpClient? httpClient = null) : IBackend
{
    public const string DefaultHost = "http://localhost:11434";
    public const string DefaultModel = "qwen3.5:4b";

    public Dictionary<string, object?> Config { get; } = config;
    private readonly HttpClient _http = httpClient ?? SharedHttpClient.Instance;

    public async Task<(string Reply, Dictionary<string, object?> Meta)> GenerateAsync(
        List<Dictionary<string, object?>> messages,
        Dictionary<string, object?>? options = null,
        int? timeoutSeconds = null)
    {
        options ??= [];
        var endpoint = ResolveEndpoint();
        var model = Config.GetOrString("model", Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? DefaultModel);
        if (string.IsNullOrWhiteSpace(model))
        {
            return ("[ERROR: Ollama model is not configured.]", new Dictionary<string, object?> { ["error"] = "missing_model" });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages
                .Select(m => new Dictionary<string, object?>
                {
                    ["role"] = (m.GetOr("role") as string) ?? "user",
                    ["content"] = m.GetOr("content") as string ?? "",
                })
                .Cast<object?>()
                .ToList(),
            ["stream"] = false,
        };

        var optionDict = new Dictionary<string, object?>();
        if (options.TryGetValue("temperature", out var temp)) optionDict["temperature"] = temp;
        if (options.TryGetValue("top_p", out var topP)) optionDict["top_p"] = topP;
        if (options.TryGetValue("max_tokens", out var maxTokens)) optionDict["num_predict"] = maxTokens;
        if (optionDict.Count > 0) payload["options"] = optionDict;

        var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? (int)Config.GetOrDouble("timeout", 180));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(timeout);
            using var response = await _http.SendAsync(request, cts.Token);
            var bodyText = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return ("[ERROR: Ollama returned an error.]", new Dictionary<string, object?> { ["error"] = bodyText, ["status"] = (int)response.StatusCode });
            }

            return ParseSuccessResponse(bodyText, model);
        }
        catch (OperationCanceledException)
        {
            return ("[ERROR: Could not connect to Ollama.]", new Dictionary<string, object?> { ["error"] = "timeout" });
        }
        catch (Exception exc)
        {
            return ("[ERROR: Could not connect to Ollama.]", new Dictionary<string, object?> { ["error"] = exc.Message });
        }
    }

    private string ResolveEndpoint()
    {
        var custom = Config.GetOrString("endpoint", "");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        var host = Environment.GetEnvironmentVariable("OLLAMA_HOST")
            ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
            ?? DefaultHost;
        return host.TrimEnd('/') + "/api/chat";
    }

    private static (string, Dictionary<string, object?>) ParseSuccessResponse(string bodyText, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var data = JsonLoader.Materialize(doc.RootElement) as Dictionary<string, object?> ?? [];

            if (data.TryGetValue("error", out var errorObj) && errorObj is not null)
            {
                return ("[ERROR: Ollama returned an error. Details: " + errorObj + "]", new Dictionary<string, object?> { ["error"] = errorObj });
            }

            var message = data.GetOrDict("message");
            var content = message?.GetOr("content") as string;
            if (!string.IsNullOrWhiteSpace(content))
            {
                return (content, new Dictionary<string, object?> { ["model"] = model, ["backend"] = "ollama", ["done"] = data.GetOr("done") });
            }

            return ("[ERROR: Empty response from Ollama model " + model + "]", new Dictionary<string, object?> { ["error"] = "empty_response", ["model"] = model, ["backend"] = "ollama" });
        }
        catch
        {
            return ("[ERROR: Unexpected response format from Ollama.]", new Dictionary<string, object?> { ["error"] = "bad_format", ["raw"] = bodyText });
        }
    }
}
