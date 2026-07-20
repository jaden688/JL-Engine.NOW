// web_engine/src/rhythm.ts
// Port of rhythm.py — computes a rhythm "mode" and momentum index from the
// current trigger, gait, behavior state, and drift pressure.

import type { BehaviorState, RhythmInfo } from "./types";

const MODES = ["flop", "twitch", "sway", "pulse", "surge"] as const;

function clamp(x: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, x));
}

export class RhythmEngine {
  compute(args: {
    lastMode: string;
    trigger: string;
    gait: string;
    behaviorState: BehaviorState;
    driftPressure: number;
    safetyOn: boolean;
  }): RhythmInfo {
    const { lastMode, trigger, gait, behaviorState, driftPressure, safetyOn } = args;

    let index =
      behaviorState.expressiveness * 0.6 +
      (trigger === "user_hyped" ? 0.3 : trigger === "user_overwhelmed" ? -0.2 : 0.05) -
      driftPressure * 0.3;
    if (gait === "sprint") index += 0.2;
    if (gait === "idle") index -= 0.2;
    if (safetyOn) index *= 0.92;
    index = clamp(index, 0, 1);

    // Momentum picks the discrete mode; hysteresis keeps it from flickering.
    let target = MODES[Math.min(MODES.length - 1, Math.floor(index * MODES.length))];
    const lastIdx = MODES.indexOf(lastMode as (typeof MODES)[number]);
    const targetIdx = MODES.indexOf(target);
    if (lastIdx >= 0 && Math.abs(targetIdx - lastIdx) > 1) {
      // move only one step per turn
      target = MODES[lastIdx + Math.sign(targetIdx - lastIdx)];
    }

    return { mode: target, index, gait };
  }
}
