using JLEngine.Core.Engine;
using JLEngine.Core.Types;
using JLEngine.Persistence;
using Xunit;

namespace JLEngine.Bridges.Tests;

public class AutopilotServiceTests
{
    [Fact]
    public void FromEnvironment_Unset_IsDisabled()
    {
        Environment.SetEnvironmentVariable("SPARKBYTE_AUTOPILOT_SECONDS", null);
        var options = AutopilotOptions.FromEnvironment();
        Assert.True(options.IntervalSeconds < 0);
    }

    [Fact]
    public void FromEnvironment_SetBelowFloor_ClampsToFiveSeconds()
    {
        Environment.SetEnvironmentVariable("SPARKBYTE_AUTOPILOT_SECONDS", "1");
        try
        {
            var options = AutopilotOptions.FromEnvironment();
            Assert.Equal(5, options.IntervalSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPARKBYTE_AUTOPILOT_SECONDS", null);
        }
    }

    [Fact]
    public async Task RunAsync_Disabled_ReturnsImmediatelyWithoutTicking()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"autopilot-test-{Guid.NewGuid()}.db");
        using var db = new SparkByteDatabase(dbPath);
        var engine = new JLEngineCore(new EngineConfig { RootDir = Path.Combine(Path.GetTempPath(), $"autopilot-engine-{Guid.NewGuid()}") });
        var service = new AutopilotService(engine, db, new AutopilotOptions { IntervalSeconds = -1 });

        var task = service.RunAsync(CancellationToken.None);
        var completed = await Task.WhenAny(task, Task.Delay(2000));
        Assert.Same(task, completed); // must return well before the 60s settle delay would apply
        Assert.False(service.Running);
    }
}
