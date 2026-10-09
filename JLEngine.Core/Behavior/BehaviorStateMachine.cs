using JLEngine.Core.Config;
using JLEngine.Core.Types;

namespace JLEngine.Core.Behavior;

/// <summary>
/// Port of Behavior.jl. A 5-row x 4-column grid of BehaviorStates loaded
/// from behavior_states.json, with trigger-based navigation and a hard
/// safety override. Internally 0-indexed throughout (Julia's machine is
/// 0-indexed internally too, with a +1 offset only at the states[][] access
/// point — collapsed away here since C# lists are already 0-indexed).
/// </summary>
public sealed class BehaviorStateMachine
{
    public List<List<BehaviorState>> States { get; }
    public Dictionary<string, (int Row, int Col)> TriggerMappings { get; }
    public int Rows { get; }
    public int Columns { get; }
    public int CurrentRow { get; private set; }
    public int CurrentCol { get; private set; }
    public Dictionary<string, object?> GatingAdvice { get; private set; }
    public double BlendWeight { get; private set; }
    public Dictionary<string, object?>? LastBlend { get; private set; }

    public BehaviorStateMachine(string configPath)
    {
        var config = JsonLoader.LoadJsonSafely(configPath);
        var states = new List<List<BehaviorState>>();

        if (config.GetOrList("states") is { } rawRows)
        {
            foreach (var rowObj in rawRows)
            {
                if (rowObj is not List<object?> row) continue;
                states.Add(row.Select(item => BehaviorStateFromDict(item as Dictionary<string, object?>)).ToList());
            }
        }

        if (states.Count == 0)
        {
            states = Enumerable.Range(0, 5).Select(_ => Enumerable.Range(0, 4).Select(_ => new BehaviorState()).ToList()).ToList();
        }

        States = states;

        var triggerMappings = new Dictionary<string, (int, int)>();
        if (config.GetOrDict("trigger_mappings") is { } rawMappings)
        {
            foreach (var (trigger, coordsObj) in rawMappings)
            {
                if (coordsObj is not List<object?> coords || coords.Count < 2) continue;
                triggerMappings[trigger] = (IntOr(coords[0], 2), IntOr(coords[1], 1));
            }
        }
        TriggerMappings = triggerMappings;

        var dims = config.GetOrDict("grid_dimensions");
        Rows = dims is not null ? IntOr(dims.GetOr("rows"), States.Count) : States.Count;
        Columns = dims is not null ? IntOr(dims.GetOr("columns"), States[0].Count) : States[0].Count;

        CurrentRow = 2;
        CurrentCol = 0;
        GatingAdvice = new Dictionary<string, object?> { ["level"] = "allow", ["weight"] = 0.0, ["reason"] = null };
        BlendWeight = 0.0;

        ComputeBlend();
    }

    public BehaviorState CurrentState() => States[CurrentRow][CurrentCol];
    public Dictionary<string, object?>? CurrentBlend() => LastBlend;

    public BehaviorState SetStateByCoords(int row, int col)
    {
        CurrentRow = Math.Clamp(row, 0, Rows - 1);
        CurrentCol = Math.Clamp(col, 0, Columns - 1);
        ComputeBlend();
        return CurrentState();
    }

    public bool SetStateByLabel(string label)
    {
        var target = label.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(target)) return false;

        for (var r = 0; r < States.Count; r++)
        {
            for (var c = 0; c < States[r].Count; c++)
            {
                var state = States[r][c];
                if (state.Name.ToLowerInvariant() == target || state.Id.ToLowerInvariant() == target)
                {
                    SetStateByCoords(r, c);
                    return true;
                }
            }
        }
        return false;
    }

    public BehaviorState TransitionByTrigger(string? trigger, string gait, Dictionary<string, object?>? gatingAdvice = null)
    {
        var advice = NormalizeAdvice(gatingAdvice ?? GatingAdvice);
        if ((advice.GetOr("level") as string ?? "allow") == "weak_block")
        {
            GatingAdvice = advice;
        }
        else
        {
            GatingAdvice = new Dictionary<string, object?> { ["level"] = "allow", ["weight"] = 0.0, ["reason"] = advice.GetOr("reason") };
        }

        if (trigger is not null && TriggerMappings.TryGetValue(trigger, out var target))
        {
            var (targetRow, targetCol) = target;
            var gaitLower = gait.Trim().ToLowerInvariant();

            targetRow = gaitLower switch
            {
                "trot" or "gallop" => Math.Min(Rows - 1, targetRow + 1),
                "sprint" => Math.Min(Rows - 1, targetRow + 2),
                "idle" => Math.Max(0, targetRow - 1),
                _ => targetRow,
            };

            if ((advice.GetOr("level") as string) == "weak_block")
            {
                var pull = FloatOr(advice.GetOr("weight"), 0.3);
                // Julia's `round(Int, x)` uses round-half-to-even (banker's rounding),
                // which matches Math.Round's default MidpointRounding.ToEven.
                targetRow = (int)Math.Round(targetRow * (1 - pull) + 2 * pull, MidpointRounding.ToEven);
            }

            if (advice.GetOrBool("safety"))
            {
                targetRow = 1;
                targetCol = 0;
            }

            SetStateByCoords(targetRow, targetCol);
        }
        else
        {
            SetStateByCoords(2, 1);
        }

        BlendWeight = FloatOr(advice.GetOr("weight"), 0.0);
        ComputeBlend();
        return CurrentState();
    }

    private static Dictionary<string, object?> NormalizeAdvice(Dictionary<string, object?>? advice)
    {
        if (advice is null)
        {
            return new Dictionary<string, object?> { ["level"] = "allow", ["weight"] = 0.0, ["safety"] = false, ["reason"] = null };
        }

        var level = (advice.GetOr("level") as string ?? "allow").ToLowerInvariant();
        if (level == "block") level = "weak_block";
        var safety = level == "safety_block" || advice.GetOrBool("safety");
        var weight = Math.Clamp(FloatOr(advice.GetOr("weight"), 0.0), 0.0, 1.0);

        return new Dictionary<string, object?>
        {
            ["level"] = level,
            ["weight"] = weight,
            ["safety"] = safety,
            ["reason"] = advice.GetOr("reason"),
        };
    }

    private void ComputeBlend()
    {
        var primary = CurrentState();
        var stabilizer = States[2][1];
        var weight = Math.Clamp(BlendWeight, 0.0, 1.0);

        if (weight <= 0.05 || (primary.Id == stabilizer.Id && primary.Name == stabilizer.Name))
        {
            LastBlend = new Dictionary<string, object?>
            {
                ["primary"] = StateSummary(primary),
                ["secondary"] = null,
                ["weights"] = (1.0, 0.0),
            };
            return;
        }

        var secondary = CurrentCol > 0 ? States[CurrentRow][CurrentCol - 1] : stabilizer;
        LastBlend = new Dictionary<string, object?>
        {
            ["primary"] = StateSummary(primary),
            ["secondary"] = StateSummary(secondary),
            ["weights"] = (Math.Round(1.0 - weight, 2), Math.Round(weight, 2)),
        };
    }

    private static Dictionary<string, object?> StateSummary(BehaviorState state) =>
        new() { ["id"] = state.Id, ["name"] = state.Name };

    private static BehaviorState BehaviorStateFromDict(Dictionary<string, object?>? data)
    {
        if (data is null) return new BehaviorState();
        return new BehaviorState
        {
            Id = data.GetOrString("id", "0,0"),
            Name = data.GetOrString("name", "Unknown"),
            Expressiveness = FloatOr(data.GetOr("expressiveness"), 0.5),
            Pacing = data.GetOrString("pacing", "normal"),
            ToneBias = data.GetOrString("tone_bias", "neutral"),
            MemoryStrictness = data.GetOrString("memory_strictness", "medium"),
        };
    }

    private static double FloatOr(object? value, double fallback) => value switch
    {
        double d => d,
        long l => l,
        int i => i,
        string s when double.TryParse(s, out var parsed) => parsed,
        _ => fallback,
    };

    private static int IntOr(object? value, int fallback) => value switch
    {
        long l => (int)l,
        int i => i,
        double d => (int)d,
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => fallback,
    };
}
