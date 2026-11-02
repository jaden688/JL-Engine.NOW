using System.Security.Cryptography;
using System.Text;

namespace JLEngine.Runtime;

/// <summary>
/// Stores the OpenRouter credential per Windows user using DPAPI. The key
/// never enters appsettings, the chat transcript, or telemetry. Environment
/// configuration remains supported for development and headless deployments.
/// </summary>
public static class OpenRouterKeyStore
{
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JL Engine",
        "openrouter.key");

    public static bool IsConfigured() => !string.IsNullOrWhiteSpace(GetKey());

    public static string? GetKey()
    {
        var environmentKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (!string.IsNullOrWhiteSpace(environmentKey)) return environmentKey;
        if (!OperatingSystem.IsWindows() || !File.Exists(KeyPath)) return null;

        try
        {
            var encrypted = File.ReadAllBytes(KeyPath);
            var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch
        {
            return null;
        }
    }

    public static void SetKey(string key)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Local encrypted key storage is currently supported on Windows. Set OPENROUTER_API_KEY for this platform.");

        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("An OpenRouter API key is required.", nameof(key));

        var directory = Path.GetDirectoryName(KeyPath)!;
        Directory.CreateDirectory(directory);
        var plaintext = Encoding.UTF8.GetBytes(key.Trim());
        var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyPath, encrypted);
    }
}
