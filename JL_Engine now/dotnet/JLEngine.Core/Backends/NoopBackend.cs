namespace JLEngine.Core.Backends;

/// <summary>Port of Backends.jl's NoopBackend: echoes the last user message
/// back verbatim. Useful for testing the engine wiring without API keys.</summary>
public sealed class NoopBackend(Dictionary<string, object?> config) : IBackend
{
    public Dictionary<string, object?> Config { get; } = config;

    public Task<(string Reply, Dictionary<string, object?> Meta)> GenerateAsync(
        List<Dictionary<string, object?>> messages,
        Dictionary<string, object?>? options = null,
        int? timeoutSeconds = null)
    {
        var userMessage = BackendMessages.MessageContent(messages);
        var reply = string.IsNullOrEmpty(userMessage)
            ? "[NOOP BACKEND] This is a stub response. No real model was called."
            : userMessage;

        var meta = new Dictionary<string, object?>
        {
            ["provider"] = "noop",
            ["status"] = "ok",
            ["model"] = "noop-stub",
            ["options"] = options,
        };

        return Task.FromResult((reply, meta));
    }
}
