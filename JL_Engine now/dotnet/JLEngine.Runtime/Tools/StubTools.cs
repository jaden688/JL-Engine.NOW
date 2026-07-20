namespace JLEngine.Runtime.Tools;

/// <summary>
/// These 6 tools exist in Julia's Tools.jl but are platform-specific
/// (Windows PnP/bluetoothctl/system_profiler shell-outs), third-party-SDK-
/// specific (Twilio SMS, Playwright browser automation), or narrow-purpose
/// one-off utilities (SillyTavern card conversion, GitHub Pages deploy,
/// a Julia-source-specific self-modification tool). They're registered here
/// with the same names/schema so the tool list stays complete and the LLM
/// gets an honest "not implemented in this port yet" response instead of a
/// missing-function error, rather than being silently dropped.
/// </summary>
public sealed class NotPortedTool(string name, string reason) : ITool
{
    public string Name => name;

    public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args) =>
        Task.FromResult(new Dictionary<string, object?>
        {
            ["error"] = $"'{Name}' is not implemented in the C# port yet ({reason}). " +
                "It exists in the original Julia engine — port it explicitly if you need it.",
        });

    public static IEnumerable<ITool> All() =>
    [
        new NotPortedTool("bluetooth_devices", "Windows PnP / bluetoothctl / system_profiler shell-outs are platform-specific"),
        new NotPortedTool("send_sms", "requires a Twilio account — port needs TWILIO_* env wiring and a decision on which SMS provider to support"),
        new NotPortedTool("playwright_interact", "requires a .NET Playwright browser context wired up alongside browse_url"),
        new NotPortedTool("metamorph", "self-modification tool tied to the Julia source layout; needs a C#-specific redesign"),
        new NotPortedTool("card_cruncher", "SillyTavern/CharacterTavern PNG character-card converter; low priority utility"),
        new NotPortedTool("github_pages_deploy", "needs a decision on deploy target/credentials for this port"),
    ];
}
