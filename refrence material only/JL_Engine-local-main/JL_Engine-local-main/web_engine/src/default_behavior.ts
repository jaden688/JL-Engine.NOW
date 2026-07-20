// web_engine/src/default_behavior.ts
// The engine's real 5×4 behavior grid + trigger mappings, mirrored from
// jl_engine_core/data/behavior_states.json so the browser engine shows true
// state names ("Engaged-Loose", not "Unknown") with zero external files.

export const DEFAULT_BEHAVIOR_CONFIG = {
  grid_dimensions: { rows: 5, columns: 4 },
  trigger_mappings: {
    user_joking: [2, 2],
    user_hyped: [3, 3],
    user_overwhelmed: [1, 0],
    user_serious_or_tired: [2, 0],
    user_engaged: [2, 1],
  } as Record<string, [number, number]>,
  states: [
    [
      { id: "0,0", name: "Dormant-Disciplined", expressiveness: 0.1, pacing: "slow", tone_bias: "neutral", memory_strictness: "high" },
      { id: "0,1", name: "Dormant-Loose", expressiveness: 0.2, pacing: "slow", tone_bias: "neutral", memory_strictness: "high" },
      { id: "0,2", name: "Dormant-Erratic", expressiveness: 0.3, pacing: "variable", tone_bias: "unpredictable", memory_strictness: "medium" },
      { id: "0,3", name: "Dormant-Chaotic", expressiveness: 0.4, pacing: "variable", tone_bias: "chaotic", memory_strictness: "low" },
    ],
    [
      { id: "1,0", name: "Stirring-Disciplined", expressiveness: 0.3, pacing: "measured", tone_bias: "focused", memory_strictness: "high" },
      { id: "1,1", name: "Stirring-Loose", expressiveness: 0.4, pacing: "measured", tone_bias: "open", memory_strictness: "medium" },
      { id: "1,2", name: "Stirring-Erratic", expressiveness: 0.5, pacing: "staccato", tone_bias: "agitated", memory_strictness: "medium" },
      { id: "1,3", name: "Stirring-Chaotic", expressiveness: 0.6, pacing: "staccato", tone_bias: "manic", memory_strictness: "low" },
    ],
    [
      { id: "2,0", name: "Engaged-Disciplined", expressiveness: 0.5, pacing: "normal", tone_bias: "direct", memory_strictness: "high" },
      { id: "2,1", name: "Engaged-Loose", expressiveness: 0.6, pacing: "normal", tone_bias: "amiable", memory_strictness: "medium" },
      { id: "2,2", name: "Engaged-Erratic", expressiveness: 0.7, pacing: "energetic", tone_bias: "playful", memory_strictness: "low" },
      { id: "2,3", name: "Engaged-Chaotic", expressiveness: 0.8, pacing: "energetic", tone_bias: "mischievous", memory_strictness: "very_low" },
    ],
    [
      { id: "3,0", name: "Volatile-Disciplined", expressiveness: 0.7, pacing: "sharp", tone_bias: "intense", memory_strictness: "high" },
      { id: "3,1", name: "Volatile-Loose", expressiveness: 0.8, pacing: "sharp", tone_bias: "passionate", memory_strictness: "medium" },
      { id: "3,2", name: "Volatile-Erratic", expressiveness: 0.9, pacing: "rapid", tone_bias: "furious", memory_strictness: "low" },
      { id: "3,3", name: "Volatile-Chaotic", expressiveness: 1.0, pacing: "rapid", tone_bias: "unhinged", memory_strictness: "very_low" },
    ],
    [
      { id: "4,0", name: "Peak-Disciplined", expressiveness: 0.85, pacing: "locked", tone_bias: "commanding", memory_strictness: "high" },
      { id: "4,1", name: "Peak-Loose", expressiveness: 0.9, pacing: "surging", tone_bias: "electric", memory_strictness: "medium" },
      { id: "4,2", name: "Peak-Erratic", expressiveness: 0.95, pacing: "explosive", tone_bias: "wild", memory_strictness: "low" },
      { id: "4,3", name: "Peak-Chaotic", expressiveness: 1.0, pacing: "explosive", tone_bias: "overload", memory_strictness: "very_low" },
    ],
  ],
};
