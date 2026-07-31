using System.Text.Json;
using JLEngine.Core.Config;
using JLEngine.Runtime.Tools;

namespace JLEngine.Bridges;

/// <summary>Port of Tools.jl's tool_card_cruncher — wraps CardCruncher's
/// parse+convert as a callable tool, writing the result as a new agent card
/// file (matching Julia's crunch_card, which writes "&lt;Name&gt;_Full.json").
/// Lives in Bridges (not Runtime.Tools, alongside the other tools) because it
/// needs CardCruncher, and Runtime can't reference Bridges — JLEngine.Host
/// registers it into each session via SessionRegistry's onSessionCreated hook.</summary>
public sealed class CardCruncherTool(string agentsDir) : ITool
{
    public string Name => "card_cruncher";

    public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var cardPath = ToolArgs.GetArg(args, "card_path", "path");
        if (string.IsNullOrEmpty(cardPath))
        {
            return Task.FromResult(new Dictionary<string, object?> { ["error"] = "card_path is required" });
        }
        if (!File.Exists(cardPath))
        {
            return Task.FromResult(new Dictionary<string, object?> { ["error"] = $"Card file not found: {cardPath}" });
        }

        var dryRun = args.TryGetValue("dry_run", out var dr) && ToolArgs.LooksTrue(dr);
        var outPathArg = ToolArgs.GetArg(args, "out_path");

        try
        {
            var card = CardCruncher.ParseCard(cardPath);
            var agent = CardCruncher.CardToAgent(card, cardPath);

            var rawName = card.GetOr("name") as string ?? "Unknown";
            var safeName = new string(rawName.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
            if (safeName.Length == 0) safeName = "UnnamedAgent";

            var outPath = string.IsNullOrEmpty(outPathArg) ? Path.Combine(agentsDir, $"{safeName}_Full.json") : outPathArg;

            if (!dryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) is { Length: > 0 } dir ? dir : agentsDir);
                File.WriteAllText(outPath, JsonSerializer.Serialize(agent, new JsonSerializerOptions { WriteIndented = true }));
            }

            return Task.FromResult(new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["output_path"] = outPath,
                ["agent_name"] = safeName,
                ["message"] = dryRun
                    ? "Dry run complete. No file written."
                    : $"Character card crunched into agent! Add an entry to Agents.mpf.json pointing at {Path.GetFileName(outPath)} to make it selectable as an operator.",
            });
        }
        catch (Exception e)
        {
            return Task.FromResult(new Dictionary<string, object?> { ["error"] = e.Message });
        }
    }
}
