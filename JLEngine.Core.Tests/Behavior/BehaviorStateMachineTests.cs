using JLEngine.Core.Behavior;
using Xunit;

namespace JLEngine.Core.Tests.Behavior;

public class BehaviorStateMachineTests
{
    private static BehaviorStateMachine NewMachine() =>
        new(Path.Combine(Path.GetTempPath(), $"nonexistent-behavior-states-{Guid.NewGuid()}.json"));

    [Fact]
    public void MissingConfigFile_FallsBackToDefault5x4Grid()
    {
        var machine = NewMachine();
        Assert.Equal(5, machine.Rows);
        Assert.Equal(4, machine.Columns);
        Assert.Equal(2, machine.CurrentRow);
        Assert.Equal(0, machine.CurrentCol);
    }

    [Fact]
    public void SafetyAdvice_ForcesHardJumpToCell_1_0()
    {
        var machine = NewMachine();
        // The safety override only applies inside the trigger-mapping-found
        // branch (Behavior.jl:138-141), so register a mapping that would
        // otherwise land far away (row 4, col 3) with a gait bonus on top,
        // to prove the safety flag overrides it unconditionally.
        machine.TriggerMappings["custom_trigger"] = (4, 3);
        var advice = new Dictionary<string, object?> { ["level"] = "weak_block", ["weight"] = 0.9, ["safety"] = true };

        var state = machine.TransitionByTrigger("custom_trigger", "sprint", advice);

        Assert.Equal(1, machine.CurrentRow);
        Assert.Equal(0, machine.CurrentCol);
        Assert.Equal(machine.States[1][0], state);
    }

    [Fact]
    public void UnmappedTrigger_DefaultsToCell_2_1()
    {
        var machine = NewMachine();
        machine.TransitionByTrigger(null, "walk");
        Assert.Equal(2, machine.CurrentRow);
        Assert.Equal(1, machine.CurrentCol);
    }

    [Fact]
    public void SetStateByCoords_ClampsToGridBounds()
    {
        var machine = NewMachine();
        machine.SetStateByCoords(100, -100);
        Assert.Equal(machine.Rows - 1, machine.CurrentRow);
        Assert.Equal(0, machine.CurrentCol);
    }
}
