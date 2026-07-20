using JLEngine.Core.Config;

namespace JLEngine.Core.Backends;

/// <summary>
/// Port of Backends.jl. Faithful port: `GenerateAsync` returns errors as a
/// "[ERROR: ...]" sentinel string embedded in the reply text (not thrown),
/// exactly matching Julia's return-contract — errors flow into memory and
/// telemetry as if they were normal assistant output. This is the current
/// actual behavior of the Julia engine and is preserved per the
/// faithful-port-first decision, even though a typed Result/exception model
/// would be more idiomatic .NET.
/// </summary>
public interface IBackend
{
    Dictionary<string, object?> Config { get; }

    Task<(string Reply, Dictionary<string, object?> Meta)> GenerateAsync(
        List<Dictionary<string, object?>> messages,
        Dictionary<string, object?>? options = null,
        int? timeoutSeconds = null);
}

public static class BackendMessages
{
    /// <summary>Port of `_message_content`: finds the last user-role message's content.</summary>
    public static string MessageContent(List<Dictionary<string, object?>> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.GetOr("role") as string == "user")
            {
                return message.GetOr("content") as string ?? "";
            }
        }
        return "";
    }
}
