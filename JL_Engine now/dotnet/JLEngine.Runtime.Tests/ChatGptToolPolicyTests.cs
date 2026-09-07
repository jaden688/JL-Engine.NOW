using JLEngine.Runtime.Tools;

namespace JLEngine.Runtime.Tests;

public class ChatGptToolPolicyTests
{
    private sealed class CountingTool : ITool
    {
        public string Name => "counting_tool";
        public int Calls { get; private set; }

        public Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
        {
            Calls++;
            return Task.FromResult<Dictionary<string, object?>>(new() { ["result"] = "called" });
        }
    }

    private static ToolSchemaEntry Schema() => new(
        "counting_tool",
        "Count calls.",
        new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
        });

    [Fact]
    public void TalkPolicyAdvertisesNoTools()
    {
        var registry = NewRegistry();
        var schemas = new Dictionary<string, ToolSchemaEntry> { ["counting_tool"] = Schema() };

        Assert.Empty(registry.BuildOpenAiToolsArray(schemas, ToolExecutionPolicy.None));
    }

    [Fact]
    public async Task TalkPolicyRejectsDispatchWithoutCallingTool()
    {
        var registry = NewRegistry();
        var tool = new CountingTool();
        registry.Register(tool);

        var result = await registry.DispatchAsync("counting_tool", [], policy: ToolExecutionPolicy.None);

        Assert.Equal(0, tool.Calls);
        Assert.Contains("disabled", result["error"]?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutePolicyPreservesFullDispatch()
    {
        var registry = NewRegistry();
        var tool = new CountingTool();
        registry.Register(tool);

        var result = await registry.DispatchAsync("counting_tool", [], policy: ToolExecutionPolicy.Full);

        Assert.Equal(1, tool.Calls);
        Assert.Equal("called", result["result"]);
    }

    private static ToolRegistry NewRegistry() =>
        new(Path.Combine(Path.GetTempPath(), $"jl-policy-{Guid.NewGuid()}"));
}
