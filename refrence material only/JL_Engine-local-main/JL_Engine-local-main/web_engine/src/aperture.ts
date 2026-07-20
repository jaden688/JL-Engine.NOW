// web_engine/src/aperture.ts
// Port of emotional_aperture.py — maps behavior + signals into an emotional
// aperture score, a discrete mode, and a sampling bias (temp/top_p nudge).

import type { ApertureState, BehaviorState, TurnSignals } from "./types";

function clamp(x: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, x));
}

function modeFromScore(score: number): ApertureState["mode"] {
  if (score < 0.25) return "SEALED";
  if (score < 0.55) return "LIMITED";
  if (score < 0.85) return "OPEN";
  return "FLOODED";
}

type PaletteEntry = { name: string; valence?: number; arousal?: number; samplingBias?: { temp?: number; top_p?: number } };

export class EmotionalAperture {
  private score = 0.45;
  private palette: PaletteEntry[] = [];

  setPalette(p?: PaletteEntry[]): void {
    this.palette = Array.isArray(p) ? p : [];
  }

  reset(): void { this.score = 0.45; }

  updateFromSignals(args: {
    behaviorState: BehaviorState;
    signals: TurnSignals;
    driftBias?: number;
  }): void {
    const { behaviorState, signals, driftBias = 0 } = args;
    // Expressiveness pulls the aperture open; negative sentiment + confusion seal it.
    const target =
      behaviorState.expressiveness * 0.5 +
      signals.arousal * 0.25 +
      (signals.sentiment + 1) / 2 * 0.2 -
      signals.confusion * 0.15 +
      driftBias;
    // Ease toward target so the aperture has inertia across turns.
    this.score = clamp(this.score * 0.6 + target * 0.4, 0, 1);
  }

  injectDriftBias(bias: number): void {
    this.score = clamp(this.score + bias, 0, 1);
  }

  private pickEmotion(): PaletteEntry | null {
    if (this.palette.length === 0) return null;
    // Choose palette entry whose valence best matches current openness.
    const target = this.score * 2 - 1; // -1..1
    let best = this.palette[0], bestDist = Infinity;
    for (const e of this.palette) {
      const v = typeof e.valence === "number" ? e.valence : 0;
      const d = Math.abs(v - target);
      if (d < bestDist) { bestDist = d; best = e; }
    }
    return best ?? null;
  }

  getState(): ApertureState {
    const mode = modeFromScore(this.score);
    // Base sampling grows with openness — mirrors apply_emotion_sampling_bias clamps.
    const emotion = this.pickEmotion();
    const bias = emotion?.samplingBias ?? {};
    const temp = clamp(0.55 + this.score * 0.45 + (bias.temp ?? 0), 0.1, 1.5);
    const top_p = clamp(0.78 + this.score * 0.22 + (bias.top_p ?? 0), 0.1, 1.0);
    return {
      score: this.score,
      mode,
      emotion: emotion?.name ?? null,
      samplingBias: { temp, top_p },
    };
  }

  focusLevel(): number { return clamp(1 - this.score, 0, 1); }
  overloadLevel(): number { return clamp(this.score - 0.7, 0, 1) / 0.3; }
}
