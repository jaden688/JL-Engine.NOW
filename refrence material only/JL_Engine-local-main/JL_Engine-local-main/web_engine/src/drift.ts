// web_engine/src/drift.ts
// Port of drift_pressure.py — a weighted regulator that measures how far the
// turn is pulling away from coherence, and returns a corrective action.

import type { ApertureState, DriftResponse, TurnSignals } from "./types";

function clamp(x: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, x));
}

export interface DriftInput {
  signals: TurnSignals;
  aperture: ApertureState;
  expressiveness: number;
  memoryDensity: number;
}

export class DriftRegulator {
  private state = 0.0; // smoothed drift across turns

  calculate(input: DriftInput): number {
    const { signals, aperture, expressiveness } = input;
    // Confusion, high arousal, and a flooded aperture all raise drift;
    // positive sentiment and directive intent lower it.
    const raw =
      signals.confusion * 0.35 +
      signals.arousal * 0.2 +
      aperture.score * 0.25 +
      expressiveness * 0.15 -
      clamp(signals.sentiment, 0, 1) * 0.2 -
      (signals.directive ? 0.1 : 0);
    this.state = clamp(this.state * 0.5 + clamp(raw, 0, 1) * 0.5, 0, 1);
    return this.state;
  }

  getResponse(pressure: number): DriftResponse {
    if (pressure >= 0.75) {
      return { pressure, action: "recenter", forceGait: "idle", forceRhythm: "flop" };
    }
    if (pressure >= 0.55) {
      return { pressure, action: "brake", forceGait: "walk", forceRhythm: "twitch" };
    }
    if (pressure >= 0.35) {
      return { pressure, action: "settle", forceGait: null, forceRhythm: null };
    }
    return { pressure, action: "hold", forceGait: null, forceRhythm: null };
  }
}
